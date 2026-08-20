using Life_Admin_Autopilot.DAL.Kernel.Time;

namespace Life_Admin_Autopilot.BLL.Kernel.Tasks;

/// <summary>
/// Time buckets — the second half of <c>taskQuery.ts</c>, and the reason this
/// file is kernel rather than slice code. "Today" is a wall-clock question, and
/// getting it wrong moves every count, digest and reminder for most of the world.
/// </summary>
public static partial class TaskQuery
{
    /// <summary>
    /// Wall-clock day boundaries in one IANA zone, expressed as UTC instants.
    /// </summary>
    public readonly record struct DayBoundaries(
        DateTime TodayStart,
        DateTime TomorrowStart,
        DateTime DayAfterTomorrowStart,
        DateTime WeekEnd);

    /// <summary>
    /// Offset of <paramref name="timezone"/> from UTC, in minutes, AT
    /// <paramref name="at"/> — so DST is accounted for.
    ///
    /// <para>
    /// <b>A null/empty zone means the product default, not 0.</b> Node returned 0,
    /// and the warning that used to sit here — that a silent UTC fallback "would
    /// move a Cairo user's whole day by two hours with nothing in the UI to reveal
    /// it" — described what this function was ALREADY doing on the absent-zone
    /// branch, because the profile field is optional and nothing ever populated it.
    /// Every count, digest and due bucket for an account with no stored zone was
    /// cut against UTC midnight, which is 02:00 or 03:00 in Cairo. The offset now
    /// comes from <see cref="AppTimeZone.Default"/> instead.
    /// </para>
    ///
    /// <para>
    /// An UNRECOGNISED zone still throws, matching Node: <c>Intl.DateTimeFormat</c>
    /// raises a RangeError there and <c>zoneOffsetMinutes</c> does not catch it, so
    /// the request becomes a 500 <c>internal_error</c>. That path is a client
    /// sending a zone it made up, which is worth surfacing; an absent one is not.
    /// </para>
    /// </summary>
    public static int ZoneOffsetMinutes(DateTime at, string? timezone)
    {
        var instant = DateTime.SpecifyKind(at, DateTimeKind.Utc);

        if (string.IsNullOrEmpty(timezone))
        {
            return (int)AppTimeZone.Default.GetUtcOffset(instant).TotalMinutes;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        return (int)zone.GetUtcOffset(instant).TotalMinutes;
    }

    /// <summary>
    /// Midnight of <paramref name="at"/>'s local day in <paramref name="timezone"/>,
    /// as a UTC instant. Ported arithmetic-for-arithmetic from Node rather than
    /// reached for via <c>TimeZoneInfo.ConvertTime</c>, so the two servers agree
    /// exactly — including on the DST edges where the two approaches differ.
    /// </summary>
    public static DateTime StartOfLocalDay(DateTime at, string? timezone)
    {
        var offsetMin = ZoneOffsetMinutes(at, timezone);
        var shifted = DateTime.SpecifyKind(at, DateTimeKind.Utc).AddMinutes(offsetMin);
        var midnightUtc = new DateTime(shifted.Year, shifted.Month, shifted.Day, 0, 0, 0, DateTimeKind.Utc);
        return midnightUtc.AddMinutes(-offsetMin);
    }

    /// <summary>Fixed 24h steps, matching Node's <c>days * 86_400_000</c> — NOT calendar days.</summary>
    public static DateTime AddDays(DateTime at, int days) => at.AddTicks(days * TimeSpan.TicksPerDay);

    public static DayBoundaries GetDayBoundaries(DateTime now, string? timezone)
    {
        var todayStart = StartOfLocalDay(now, timezone);
        return new DayBoundaries(
            todayStart,
            AddDays(todayStart, 1),
            AddDays(todayStart, 2),

            // "This week" means the next 7 DAYS, not the calendar week — a
            // Sunday-night view of a calendar week is empty and useless.
            AddDays(todayStart, 7));
    }
}
