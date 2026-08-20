using Life_Admin_Autopilot.DAL.Kernel.Time;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The product's default zone, and the resolvers that reach for it.
///
/// <para>
/// These tests exist because the thing they pin used to be UTC, and being UTC was
/// invisible: nothing threw, nothing logged, and every instant the server derived
/// for an account with no stored zone simply arrived two or three hours early. The
/// only way that shows up as a failure rather than as a user complaint is if
/// something asserts on it.
/// </para>
/// </summary>
public sealed class AppTimeZoneTests
{
    /// <summary>Inside Egyptian DST — Egypt runs +03:00 from late April to late October.</summary>
    private static readonly DateTimeOffset Summer = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Outside it, where Egypt runs +02:00.</summary>
    private static readonly DateTimeOffset Winter = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void the_default_is_egypt()
    {
        Assert.Equal("Africa/Cairo", AppTimeZone.DefaultId);
    }

    [Fact]
    public void the_default_resolves_on_this_host()
    {
        // If this fails the host cannot see the tz database, and AppTimeZone has
        // silently degraded to UTC — which is the exact failure it was written to
        // prevent, so it must not pass quietly.
        Assert.NotEqual(TimeZoneInfo.Utc, AppTimeZone.Default);
    }

    /// <summary>
    /// The reason the default is a ZONE and not a fixed offset. Egypt reinstated DST
    /// in 2023, so a hardcoded +02:00 or +03:00 is wrong for about half of every
    /// year — and wrong in a way that only shows up months after it ships.
    /// </summary>
    [Fact]
    public void the_default_observes_egyptian_daylight_saving()
    {
        Assert.Equal(TimeSpan.FromHours(3), AppTimeZone.Default.GetUtcOffset(Summer));
        Assert.Equal(TimeSpan.FromHours(2), AppTimeZone.Default.GetUtcOffset(Winter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Not/AZone")]
    public void an_absent_or_unusable_zone_resolves_to_the_default(string? zone)
    {
        Assert.Equal(AppTimeZone.Default, AppTimeZone.Resolve(zone));
    }

    /// <summary>
    /// The whole point: a stored zone WINS. A user who picked Europe/Berlin in
    /// Profile must not be quietly pulled back to Cairo, which would be the same
    /// class of bug as the UTC fallback with a different constant in it.
    /// </summary>
    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("Asia/Kolkata")]
    [InlineData("America/New_York")]
    [InlineData("UTC")]
    public void a_stored_zone_always_beats_the_default(string zone)
    {
        Assert.Equal(zone, AppTimeZone.Resolve(zone).Id);
        Assert.Equal(zone, AppTimeZone.ResolveId(zone));
    }

    /// <summary>
    /// <c>ResolveId</c> hands the caller's own string back untouched, including one
    /// this host cannot resolve. Callers feed it to Mongo's <c>$dateToString</c> and
    /// to prompt text, and a zone this process does not know may still be one the
    /// database does — swallowing it into the default would be a silent downgrade.
    /// </summary>
    [Fact]
    public void resolve_id_passes_an_unknown_zone_through_rather_than_swallowing_it()
    {
        Assert.Equal("Mars/Olympus_Mons", AppTimeZone.ResolveId("Mars/Olympus_Mons"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void resolve_id_falls_back_only_when_there_is_nothing_to_pass_through(string? zone)
    {
        Assert.Equal(AppTimeZone.DefaultId, AppTimeZone.ResolveId(zone));
    }
}
