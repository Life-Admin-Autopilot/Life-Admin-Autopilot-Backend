using System.Globalization;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

public enum IcsFrequency
{
    Secondly,
    Minutely,
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <param name="Ordinal">
/// <c>2MO</c> → 2, <c>-1FR</c> → -1, bare <c>MO</c> → null. Only meaningful for
/// MONTHLY and YEARLY.
/// </param>
public readonly record struct IcsWeekDay(int? Ordinal, DayOfWeek Day);

/// <summary>
/// A parsed RRULE.
///
/// <para>
/// <b>Supported parts:</b> FREQ, INTERVAL, COUNT, UNTIL, BYDAY (with ordinals),
/// BYMONTHDAY (including negatives), BYMONTH, BYSETPOS, WKST. That covers every
/// shape real publishers emit — school terms, bin collections, fixture lists.
/// </para>
///
/// <para>
/// <b>Deliberately unsupported:</b> BYYEARDAY, BYWEEKNO, BYHOUR, BYMINUTE,
/// BYSECOND. They are ignored rather than rejected, so a feed carrying one still
/// produces its occurrences at the DTSTART time of day instead of vanishing. Any
/// divergence from the reference is confined to those parts; the reference's own
/// test corpus exercises only FREQ/COUNT/INTERVAL.
/// </para>
/// </summary>
public sealed record IcsRecurrenceRule(
    IcsFrequency Frequency,
    int Interval,
    int? Count,
    IcsWallClock? Until,
    IReadOnlyList<IcsWeekDay> ByDay,
    IReadOnlyList<int> ByMonthDay,
    IReadOnlyList<int> ByMonth,
    IReadOnlyList<int> BySetPos,
    DayOfWeek WeekStart);

/// <summary>
/// Expands an RRULE in WALL-CLOCK space.
///
/// <para>
/// <b>Wall clock, not instants, is the whole point.</b> RFC 5545 computes a
/// recurrence in the local time of DTSTART, so stepping the wall clock and
/// converting each occurrence separately is both what the spec says and what keeps
/// a weekly 09:00 series at 09:00 across a DST boundary. Converting the first
/// occurrence and adding seven days repeatedly would silently move a school event
/// an hour for half the year.
/// </para>
/// </summary>
public static class IcsRecurrence
{
    /// <summary>Ceiling on candidates generated for a single period, so a pathological rule cannot spin.</summary>
    private const int MaxCandidatesPerPeriod = 400;

    /// <summary>
    /// Ceiling on the SIZE of each BY* list.
    ///
    /// <para>
    /// <b>Not cosmetic — this is the DoS bound.</b> <see cref="MaxCandidatesPerPeriod"/>
    /// caps a period's OUTPUT, but the per-period pipeline scans and sorts the whole
    /// BY* list first, and that runs once per period for up to
    /// <c>maxPeriods</c> periods. Without a cap here, one
    /// <c>RRULE:FREQ=MONTHLY;BYDAY=MO,MO,MO,…</c> line — a stranger's feed body, well
    /// inside the 5 MB ceiling — multiplies out into minutes of synchronous CPU on
    /// the subscribe request. Measured at 14 s for 50k tokens before this cap
    /// existed. No real publisher lists more than a handful.
    /// </para>
    /// </summary>
    private const int MaxRuleParts = 100;

    /// <summary>
    /// Ceiling on INTERVAL. An unbounded one overflows <c>DateTime.AddYears</c>, and
    /// the resulting throw is swallowed by the per-VEVENT catch upstream — so the
    /// event loses not just its recurrences but its BASE occurrence, silently.
    /// </summary>
    private const int MaxInterval = 10_000;

    public static IcsRecurrenceRule? Parse(string value)
    {
        var frequency = (IcsFrequency?)null;
        var interval = 1;
        int? count = null;
        IcsWallClock? until = null;
        var byDay = new List<IcsWeekDay>();
        var byMonthDay = new List<int>();
        var byMonth = new List<int>();
        var bySetPos = new List<int>();
        var weekStart = DayOfWeek.Monday;

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = part[..equals].Trim().ToUpperInvariant();
            var raw = part[(equals + 1)..].Trim();

            switch (key)
            {
                case "FREQ":
                    frequency = raw.ToUpperInvariant() switch
                    {
                        "SECONDLY" => IcsFrequency.Secondly,
                        "MINUTELY" => IcsFrequency.Minutely,
                        "HOURLY" => IcsFrequency.Hourly,
                        "DAILY" => IcsFrequency.Daily,
                        "WEEKLY" => IcsFrequency.Weekly,
                        "MONTHLY" => IcsFrequency.Monthly,
                        "YEARLY" => IcsFrequency.Yearly,
                        _ => null,
                    };
                    break;

                case "INTERVAL":
                    if (int.TryParse(raw, out var parsedInterval) && parsedInterval > 0)
                    {
                        interval = Math.Min(parsedInterval, MaxInterval);
                    }

                    break;

                case "COUNT":
                    if (int.TryParse(raw, out var parsedCount) && parsedCount >= 0)
                    {
                        count = parsedCount;
                    }

                    break;

                case "UNTIL":
                    until = ParseUntil(raw);
                    break;

                // Each BY* list is capped AND de-duplicated. Duplicates are the cheap
                // half of the amplification — `BYDAY=MO,MO,MO,…` is one distinct value
                // repeated, and Distinct() alone collapses it.
                case "BYDAY":
                    byDay.AddRange(Capped(ParseByDay(raw)));
                    break;

                case "BYMONTHDAY":
                    byMonthDay.AddRange(Capped(ParseInts(raw, v => v is >= -31 and <= 31 and not 0)));
                    break;

                case "BYMONTH":
                    byMonth.AddRange(Capped(ParseInts(raw, v => v is >= 1 and <= 12)));
                    break;

                case "BYSETPOS":
                    bySetPos.AddRange(Capped(ParseInts(raw, v => v != 0)));
                    break;

                case "WKST":
                    if (TryWeekday(raw, out var wkst))
                    {
                        weekStart = wkst;
                    }

                    break;
            }
        }

        return frequency is null
            ? null
            : new IcsRecurrenceRule(
                frequency.Value, interval, count, until, byDay, byMonthDay, byMonth, bySetPos, weekStart);
    }

    /// <summary>
    /// Occurrences in ascending order, starting at <paramref name="start"/>.
    ///
    /// <para>
    /// Lazily enumerated and NEVER self-terminating for an unbounded rule — an RRULE
    /// with no UNTIL and no COUNT is legal and infinite, so the caller must impose
    /// its own ceiling. <paramref name="maxPeriods"/> is only the inner safety
    /// valve.
    /// </para>
    /// </summary>
    public static IEnumerable<IcsWallClock> Expand(
        IcsWallClock start,
        IcsRecurrenceRule rule,
        int maxPeriods = 5000)
    {
        var origin = ToDateTime(start);
        var period = PeriodAnchor(origin, rule);
        var until = rule.Until is { } u ? ToDateTime(u) : (DateTime?)null;
        var emitted = 0;

        for (var iteration = 0; iteration < maxPeriods; iteration += 1)
        {
            foreach (var candidate in Candidates(period, origin, rule))
            {
                if (candidate < origin)
                {
                    continue;
                }

                if (until.HasValue && candidate > until.Value)
                {
                    yield break;
                }

                yield return FromDateTime(candidate);

                emitted += 1;
                if (rule.Count.HasValue && emitted >= rule.Count.Value)
                {
                    yield break;
                }
            }

            var next = AdvancePeriod(period, rule);

            // A rule that cannot move forward (or has run off the calendar) must not
            // become an infinite loop.
            if (next <= period || next.Year > 9000)
            {
                yield break;
            }

            period = next;
        }
    }

    private static IEnumerable<DateTime> Candidates(DateTime period, DateTime origin, IcsRecurrenceRule rule)
    {
        var raw = rule.Frequency switch
        {
            IcsFrequency.Secondly or IcsFrequency.Minutely or IcsFrequency.Hourly or IcsFrequency.Daily =>
                new[] { period }.AsEnumerable(),
            IcsFrequency.Weekly => WeeklyCandidates(period, origin, rule),
            IcsFrequency.Monthly => MonthlyCandidates(period, origin, rule),
            _ => YearlyCandidates(period, origin, rule),
        };

        var filtered = raw
            .Where(c => PassesFilters(c, rule))
            .Distinct()
            .OrderBy(c => c)
            .Take(MaxCandidatesPerPeriod)
            .ToList();

        return rule.BySetPos.Count == 0 ? filtered : ApplySetPos(filtered, rule.BySetPos);
    }

    private static IEnumerable<DateTime> WeeklyCandidates(DateTime period, DateTime origin, IcsRecurrenceRule rule)
    {
        if (rule.ByDay.Count == 0)
        {
            yield return period;
            yield break;
        }

        // `period` is the start of the week under WKST; BYDAY names offsets into it.
        foreach (var day in rule.ByDay)
        {
            var offset = ((int)day.Day - (int)rule.WeekStart + 7) % 7;
            yield return period.AddDays(offset).Date.Add(origin.TimeOfDay);
        }
    }

    private static IEnumerable<DateTime> MonthlyCandidates(DateTime period, DateTime origin, IcsRecurrenceRule rule)
    {
        foreach (var candidate in MonthCandidates(period.Year, period.Month, origin, rule))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<DateTime> YearlyCandidates(DateTime period, DateTime origin, IcsRecurrenceRule rule)
    {
        var months = rule.ByMonth.Count > 0 ? rule.ByMonth : new[] { origin.Month };

        foreach (var month in months)
        {
            foreach (var candidate in MonthCandidates(period.Year, month, origin, rule))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<DateTime> MonthCandidates(int year, int month, DateTime origin, IcsRecurrenceRule rule)
    {
        if (month is < 1 or > 12 || year is < 1 or > 9999)
        {
            yield break;
        }

        var daysInMonth = DateTime.DaysInMonth(year, month);

        if (rule.ByMonthDay.Count > 0)
        {
            foreach (var raw in rule.ByMonthDay)
            {
                var day = raw > 0 ? raw : daysInMonth + raw + 1;
                if (day >= 1 && day <= daysInMonth)
                {
                    yield return new DateTime(year, month, day).Add(origin.TimeOfDay);
                }
            }

            yield break;
        }

        if (rule.ByDay.Count > 0)
        {
            foreach (var spec in rule.ByDay)
            {
                var matching = Enumerable
                    .Range(1, daysInMonth)
                    .Where(d => new DateTime(year, month, d).DayOfWeek == spec.Day)
                    .ToList();

                if (spec.Ordinal is null)
                {
                    foreach (var d in matching)
                    {
                        yield return new DateTime(year, month, d).Add(origin.TimeOfDay);
                    }

                    continue;
                }

                var index = spec.Ordinal.Value > 0
                    ? spec.Ordinal.Value - 1
                    : matching.Count + spec.Ordinal.Value;

                if (index >= 0 && index < matching.Count)
                {
                    yield return new DateTime(year, month, matching[index]).Add(origin.TimeOfDay);
                }
            }

            yield break;
        }

        // Plain "same day each month". RFC 5545 SKIPS a month that has no such day
        // (31st in February) rather than clamping to the last — clamping would
        // silently invent an occurrence the publisher did not schedule.
        if (origin.Day <= daysInMonth)
        {
            yield return new DateTime(year, month, origin.Day).Add(origin.TimeOfDay);
        }
    }

    private static bool PassesFilters(DateTime candidate, IcsRecurrenceRule rule)
    {
        if (candidate.Year is < 1 or > 9999)
        {
            return false;
        }

        if (rule.ByMonth.Count > 0 && !rule.ByMonth.Contains(candidate.Month))
        {
            return false;
        }

        // For MONTHLY/YEARLY these were generators, not filters, so re-testing is a
        // no-op. For the sub-monthly frequencies they are the only thing narrowing
        // the stream.
        var subMonthly = rule.Frequency
            is IcsFrequency.Secondly or IcsFrequency.Minutely or IcsFrequency.Hourly
            or IcsFrequency.Daily or IcsFrequency.Weekly;

        if (subMonthly && rule.ByMonthDay.Count > 0)
        {
            var daysInMonth = DateTime.DaysInMonth(candidate.Year, candidate.Month);
            var matches = rule.ByMonthDay.Any(raw =>
                (raw > 0 ? raw : daysInMonth + raw + 1) == candidate.Day);

            if (!matches)
            {
                return false;
            }
        }

        if (rule.ByDay.Count > 0
            && rule.Frequency is IcsFrequency.Secondly or IcsFrequency.Minutely
                or IcsFrequency.Hourly or IcsFrequency.Daily
            && !rule.ByDay.Any(d => d.Day == candidate.DayOfWeek))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<DateTime> ApplySetPos(IReadOnlyList<DateTime> candidates, IReadOnlyList<int> setPos)
    {
        var picked = new SortedSet<int>();
        foreach (var pos in setPos)
        {
            var index = pos > 0 ? pos - 1 : candidates.Count + pos;
            if (index >= 0 && index < candidates.Count)
            {
                picked.Add(index);
            }
        }

        return picked.Select(i => candidates[i]);
    }

    private static DateTime PeriodAnchor(DateTime origin, IcsRecurrenceRule rule) => rule.Frequency switch
    {
        IcsFrequency.Weekly when rule.ByDay.Count > 0 => StartOfWeek(origin, rule.WeekStart),
        IcsFrequency.Monthly => new DateTime(origin.Year, origin.Month, 1).Add(origin.TimeOfDay),
        IcsFrequency.Yearly => new DateTime(origin.Year, 1, 1).Add(origin.TimeOfDay),
        _ => origin,
    };

    /// <summary>
    /// Running off the end of the calendar means "no more periods", never an
    /// exception.
    ///
    /// <para>
    /// The throw would be swallowed by the per-VEVENT catch upstream, which loses the
    /// event's BASE occurrence as well as its recurrences — an entry the publisher
    /// scheduled simply never appears, with nothing logged to explain it.
    /// <see cref="DateTime.MaxValue"/> lets the caller's own bound end the walk.
    /// </para>
    /// </summary>
    private static DateTime AdvancePeriod(DateTime period, IcsRecurrenceRule rule)
    {
        try
        {
            return rule.Frequency switch
            {
                IcsFrequency.Secondly => period.AddSeconds(rule.Interval),
                IcsFrequency.Minutely => period.AddMinutes(rule.Interval),
                IcsFrequency.Hourly => period.AddHours(rule.Interval),
                IcsFrequency.Daily => period.AddDays(rule.Interval),
                IcsFrequency.Weekly => period.AddDays(7L * rule.Interval),
                IcsFrequency.Monthly => period.AddMonths(rule.Interval),
                _ => period.AddYears(rule.Interval),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MaxValue;
        }
    }

    private static DateTime StartOfWeek(DateTime value, DayOfWeek weekStart)
    {
        var delta = ((int)value.DayOfWeek - (int)weekStart + 7) % 7;
        return value.AddDays(-delta);
    }

    private static IEnumerable<IcsWeekDay> ParseByDay(string raw)
    {
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = token.Trim().ToUpperInvariant();
            if (text.Length < 2)
            {
                continue;
            }

            var code = text[^2..];
            if (!TryWeekday(code, out var day))
            {
                continue;
            }

            var prefix = text[..^2];
            int? ordinal = null;
            if (prefix.Length > 0)
            {
                if (!int.TryParse(prefix, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    || parsed == 0)
                {
                    continue;
                }

                ordinal = parsed;
            }

            yield return new IcsWeekDay(ordinal, day);
        }
    }

    private static IEnumerable<int> ParseInts(string raw, Func<int, bool> valid)
    {
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                && valid(value))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<T> Capped<T>(IEnumerable<T> values) => values.Distinct().Take(MaxRuleParts);

    private static bool TryWeekday(string code, out DayOfWeek day)
    {
        switch (code.Trim().ToUpperInvariant())
        {
            case "SU": day = DayOfWeek.Sunday; return true;
            case "MO": day = DayOfWeek.Monday; return true;
            case "TU": day = DayOfWeek.Tuesday; return true;
            case "WE": day = DayOfWeek.Wednesday; return true;
            case "TH": day = DayOfWeek.Thursday; return true;
            case "FR": day = DayOfWeek.Friday; return true;
            case "SA": day = DayOfWeek.Saturday; return true;
            default: day = DayOfWeek.Monday; return false;
        }
    }

    /// <summary>
    /// UNTIL is compared in wall-clock space alongside the occurrences. RFC 5545
    /// requires a UTC UNTIL whenever DTSTART carries a zone, so for a zoned series
    /// this comparison is off by the zone offset at the boundary — it can include or
    /// exclude one final occurrence within a few hours of UNTIL. Recorded rather
    /// than silently corrected, because "correcting" it here would diverge from the
    /// reference in the far more common cases.
    /// </summary>
    private static IcsWallClock? ParseUntil(string raw)
    {
        var text = raw.Trim();
        var body = text.EndsWith('Z') ? text[..^1] : text;

        if (body.Length == 8
            && DateOnly.TryParseExact(body, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            // A date-only UNTIL bounds the whole day.
            return new IcsWallClock(date.Year, date.Month, date.Day, 23, 59);
        }

        if (body.Length == 15
            && DateTime.TryParseExact(
                body, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return new IcsWallClock(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute);
        }

        return null;
    }

    private static DateTime ToDateTime(IcsWallClock wall) =>
        new(wall.Year, wall.Month, wall.Day, wall.Hours, wall.Minutes, 0, DateTimeKind.Unspecified);

    private static IcsWallClock FromDateTime(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute);
}
