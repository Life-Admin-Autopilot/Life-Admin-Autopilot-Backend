using Life_Admin_Autopilot.BLL.Features.Clarifications;

namespace Life_Admin_Autopilot.Tests.Features.Clarifications;

/// <summary>
/// A typed answer's clock time, and the <c>Z</c> that moved it three hours.
///
/// <para>
/// <b>The incident.</b> 2026-08-22, Africa/Cairo. The card asked "ما هو موعد ماتش
/// البادل يوم الثلاثاء؟" and the user typed "الساعة 4 العصر" — four in the
/// afternoon. It filed at 19:00. The interpreter's system prompt says dueAt is
/// "in the user's local time" and hands it a NOW anchor written <c>+03:00</c>,
/// and most of the time it answers <c>2026-08-25T16:00:00</c>, which
/// <see cref="HoldTimeNormalizer"/> reads in the caller's zone and stores as
/// 13:00Z. That run it answered <c>2026-08-25T16:00:00Z</c> — the same wall clock
/// with a suffix nobody asked for — and an explicit offset is unambiguous by
/// definition, so it went through untouched.
/// </para>
///
/// <para>
/// Typing the same answer again reproduced the CORRECT result, which is what made
/// it read as "+3 hours, sometimes" rather than as a bug with a shape. These tests
/// pin the rule so the intermittency cannot come back: the model is asked for a
/// local wall clock, so that is what its answer is read as, whatever it suffixes.
/// </para>
/// </summary>
public sealed class TypedAnswerTimeTests
{
    /// <summary>The exact value from the padel match, and the exact fix.</summary>
    [Fact]
    public void a_trailing_Z_is_read_as_the_users_wall_clock()
    {
        var read = CustomAnswerInterpreter.LocalWallClock("2026-08-25T16:00:00Z", "Africa/Cairo");

        Assert.Equal("2026-08-25T16:00:00", read);
        Assert.Equal(
            new DateTime(2026, 8, 25, 13, 0, 0, DateTimeKind.Utc),
            HoldTimeNormalizer.Normalize(read, "Africa/Cairo"));
    }

    /// <summary>
    /// A real offset means the model did what it was asked, and the instant it names
    /// is already right. Stripping this would break the answers that WORK.
    /// </summary>
    [Theory]
    [InlineData("2026-08-25T16:00:00+03:00")]
    [InlineData("2026-08-25T16:00:00-07:00")]
    public void an_explicit_offset_is_left_alone(string iso)
    {
        Assert.Equal(iso, CustomAnswerInterpreter.LocalWallClock(iso, "Africa/Cairo"));
    }

    [Fact]
    public void a_naive_value_is_already_what_the_rule_wants()
    {
        Assert.Equal(
            "2026-08-25T16:00:00",
            CustomAnswerInterpreter.LocalWallClock("2026-08-25T16:00:00", "Africa/Cairo"));
    }

    /// <summary>
    /// No usable zone, so there is nothing to re-read the wall clock AS. The value
    /// stands and the normaliser's own UTC fallback applies — the behaviour before
    /// this rule existed, which is the right thing to fall back to.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus")]
    public void an_unusable_zone_changes_nothing(string? timezone)
    {
        Assert.Equal(
            "2026-08-25T16:00:00Z",
            CustomAnswerInterpreter.LocalWallClock("2026-08-25T16:00:00Z", timezone));
    }

    /// <summary>
    /// In UTC the two readings are the same instant, so the rule is a no-op there —
    /// which is why it can be applied without asking whether it is safe to.
    /// </summary>
    [Fact]
    public void a_user_in_UTC_is_unaffected_either_way()
    {
        var read = CustomAnswerInterpreter.LocalWallClock("2026-08-25T16:00:00Z", "UTC");

        Assert.Equal(
            HoldTimeNormalizer.Normalize("2026-08-25T16:00:00Z", "UTC"),
            HoldTimeNormalizer.Normalize(read, "UTC"));
    }

    /// <summary>
    /// A lone "Z" is not a datetime with a suffix. Handed back whole rather than
    /// half-stripped, so it fails the caller's own ISO check instead of here.
    /// </summary>
    [Fact]
    public void something_that_is_not_a_datetime_is_not_mangled()
    {
        Assert.Equal("Z", CustomAnswerInterpreter.LocalWallClock("Z", "Africa/Cairo"));
    }
}
