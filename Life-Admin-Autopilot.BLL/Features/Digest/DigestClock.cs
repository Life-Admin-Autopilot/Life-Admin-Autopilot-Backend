using System.Globalization;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>
/// The digest's two time questions: is this zone real, and what local day is it?
/// </summary>
public static class DigestClock
{
    /// <summary>
    /// Port of <c>safeTimezone</c>.
    ///
    /// <para>
    /// <b>This endpoint is the one place a bad zone must NOT throw.</b> Everywhere
    /// else — <c>TaskQuery.ZoneOffsetMinutes</c>, <c>/me/tasks/counts</c>,
    /// <c>ImportedTimeResolver</c> — an unrecognised zone is deliberately a 500,
    /// matching Node's uncaught <c>Intl</c> RangeError. Here Node catches it, logs
    /// <c>daily-digest:invalid-timezone</c> and falls back, because the dashboard's
    /// headline read is contractually not allowed to fail on a client typo.
    /// Verified in the contract: <c>?tz=Not/AZone</c> returns 200.
    /// </para>
    /// </summary>
    public static string? SafeTimezone(string? timezone, ILogger logger)
    {
        if (string.IsNullOrEmpty(timezone))
        {
            return null;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return timezone;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("daily-digest:invalid-timezone timezone={Timezone}", timezone);
            return null;
        }
    }

    /// <summary>
    /// Port of <c>localDateKey</c> — <c>YYYY-MM-DD</c> in the caller's zone.
    ///
    /// <para>
    /// <b>PORTED BUG — the fallback zone here is not UTC.</b> Node builds this with
    /// <c>new Intl.DateTimeFormat('en-CA', { timeZone: timezone, … })</c>, and when
    /// <c>timezone</c> is <c>undefined</c> Intl does NOT fall back to UTC — it falls
    /// back to the HOST's zone. Every other time decision in the digest treats an
    /// absent zone as UTC: <c>dayBoundaries</c> goes through
    /// <c>zoneOffsetMinutes</c>, which returns 0, and the busiest-day grouping
    /// passes <c>timezone ?? 'UTC'</c> to <c>$dateToString</c> explicitly.
    /// </para>
    ///
    /// <para>
    /// So on a caller who sends no <c>tz</c>, and on one whose <c>tz</c> was invalid
    /// and got dropped, <c>localDate</c> is the SERVER's calendar date while every
    /// count in the payload is bucketed against UTC midnight. On a host that is not
    /// itself on UTC the two disagree for part of every day. Reproduced rather than
    /// corrected: <c>localDate</c> is also the digest cache key, so "fixing" it to
    /// UTC would move which row a request reads and silently change what a real user
    /// is served. Logged as a follow-up against the Node source instead.
    /// </para>
    /// </summary>
    public static string LocalDateKey(DateTime at, string? timezone)
    {
        // Intl's default-zone behaviour, spelled out. TimeZoneInfo.Local reads the
        // same tz database ICU does, so the two servers agree on one host.
        var effective = timezone ?? TimeZoneInfo.Local.Id;

        var shifted = DateTime
            .SpecifyKind(at, DateTimeKind.Utc)
            .AddMinutes(TaskQuery.ZoneOffsetMinutes(at, effective));

        return shifted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
