using System.Globalization;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Time;
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
    /// <b>FIXED PORTED BUG — the absent-zone fallback was the host's own zone.</b>
    /// Node builds this with <c>new Intl.DateTimeFormat('en-CA', { timeZone:
    /// timezone, … })</c>, and when <c>timezone</c> is <c>undefined</c> Intl does
    /// NOT fall back to UTC — it falls back to the HOST's zone. This port reproduced
    /// that with <c>TimeZoneInfo.Local.Id</c>, so on a caller who sends no
    /// <c>tz</c>, and on one whose <c>tz</c> was invalid and got dropped,
    /// <c>localDate</c> named the SERVER's calendar date while every count in the
    /// same payload was bucketed against UTC midnight. Two different calendars in
    /// one response, disagreeing for part of every day, and neither of them the
    /// user's.
    /// </para>
    ///
    /// <para>
    /// Both halves now resolve to <see cref="AppTimeZone.Default"/> when the zone is
    /// absent — <c>dayBoundaries</c> through <c>TaskQuery.ZoneOffsetMinutes</c>, the
    /// busiest-day grouping through <c>AppTimeZone.ResolveId</c>, and this key here
    /// — so the payload is internally consistent and identical on every host, which
    /// the old pair could not be. <c>localDate</c> is also the digest cache key, so
    /// this moves which row an absent-<c>tz</c> request reads: the first digest
    /// after deploy is recomputed rather than read, which is what the cache is for.
    /// </para>
    /// </summary>
    public static string LocalDateKey(DateTime at, string? timezone)
    {
        var shifted = DateTime
            .SpecifyKind(at, DateTimeKind.Utc)
            .AddMinutes(TaskQuery.ZoneOffsetMinutes(at, timezone));

        return shifted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
