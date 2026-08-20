namespace Life_Admin_Autopilot.DAL.Kernel.Time;

/// <summary>
/// The one place the product's default timezone is written down.
///
/// <para>
/// <b>Why this type exists at all.</b> The profile's <c>Timezone</c> is optional,
/// and until now every consumer that met an absent one reached for UTC on its own
/// — eleven separate <c>?? "UTC"</c> and <c>return TimeZoneInfo.Utc</c> sites
/// across three projects. The product's users are in Egypt, which is UTC+2 in
/// winter and UTC+3 under DST, so an absent zone put every derived instant two or
/// three hours early: reminders fired early, "today" rolled over at 02:00 or
/// 03:00 local, month buckets cut the wrong side of midnight, and the planning
/// agent grounded "tomorrow 9am" against the wrong clock. None of it surfaced as
/// an error anywhere.
/// </para>
///
/// <para>
/// <b>The default is a real IANA zone, not a fixed offset.</b> Egypt reinstated
/// daylight saving in 2023 (last Friday of April to the last Thursday of October),
/// so a hardcoded <c>+02:00</c> or <c>+03:00</c> is wrong for roughly half of
/// every year. <c>Africa/Cairo</c> carries the transition rules, which is the
/// whole point of naming a zone rather than an offset.
/// </para>
///
/// <para>
/// <b>The default never overrides a stored zone.</b> It is what an account gets at
/// signup and what a resolver uses when it is handed nothing usable. A user who
/// picks a zone in Profile wins everywhere, unconditionally — see
/// <see cref="Resolve"/>.
/// </para>
/// </summary>
public static class AppTimeZone
{
    /// <summary>
    /// The IANA id new accounts are provisioned with and every resolver falls back
    /// to. Egypt, because that is where this product's users are.
    /// </summary>
    public const string DefaultId = "Africa/Cairo";

    /// <summary>
    /// The Windows identifier for the same zone.
    ///
    /// <para>
    /// .NET 6+ resolves IANA ids on Windows through ICU, so <see cref="DefaultId"/>
    /// works on every supported host and this is never reached in practice. It is
    /// here for the one host that ships without ICU (a self-contained build with
    /// <c>InvariantGlobalization</c>, which several container images set by
    /// default), where the IANA lookup throws and silently falling back to UTC
    /// would reintroduce the exact bug this type exists to remove.
    /// </para>
    /// </summary>
    private const string DefaultWindowsId = "Egypt Standard Time";

    /// <summary>
    /// <see cref="DefaultId"/> as a resolved zone, looked up once.
    ///
    /// <para>
    /// <c>Lazy</c> rather than a static initialiser so a host that cannot resolve
    /// it fails on first use with a readable stack rather than inside a type
    /// initialiser, and so the tz database read happens once rather than per call —
    /// the reminder tick resolves a zone per task and a batch carries a hundred.
    /// </para>
    /// </summary>
    private static readonly Lazy<TimeZoneInfo> LazyDefault = new(() =>
    {
        foreach (var id in new[] { DefaultId, DefaultWindowsId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the next spelling.
            }
        }

        // Nothing left to try. UTC is wrong, but a briefing that renders three
        // hours out beats a 500 on every request the process serves.
        return TimeZoneInfo.Utc;
    });

    /// <summary>The default zone, resolved. Never null, never throws.</summary>
    public static TimeZoneInfo Default => LazyDefault.Value;

    /// <summary>
    /// The caller's zone, or the default when they have none the host recognises.
    ///
    /// <para>
    /// The two failure modes are deliberately collapsed: an absent zone (a profile
    /// written before the default existed) and a stored zone this host cannot
    /// resolve (a tzdata version skew, or a value a client typed) both land on the
    /// default. Neither is a reason to fail a request, and both used to land on
    /// UTC.
    /// </para>
    /// </summary>
    public static TimeZoneInfo Resolve(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return Default;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Default;
        }
    }

    /// <summary>
    /// The id to hand something that wants a zone NAME rather than a resolved zone
    /// — a Mongo <c>$dateToString</c> stage, or a prompt line telling the model
    /// which clock the user is on.
    ///
    /// <para>
    /// Returns the caller's own string untouched when it is usable, so a zone this
    /// host cannot resolve but Mongo can still reaches the aggregation intact.
    /// Only an absent or blank value becomes <see cref="DefaultId"/>.
    /// </para>
    /// </summary>
    public static string ResolveId(string? timezone) =>
        string.IsNullOrWhiteSpace(timezone) ? DefaultId : timezone;
}
