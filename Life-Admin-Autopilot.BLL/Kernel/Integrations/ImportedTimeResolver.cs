using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.BLL.Kernel.Integrations;

/// <summary>Thrown rather than defaulting to UTC. See <see cref="ImportedTimeResolver"/>.</summary>
public sealed class TimezoneRequiredException : Exception
{
    public TimezoneRequiredException()
        : base("A date-only import cannot be resolved without the user timezone.")
    {
    }
}

/// <param name="DueAt">The instant the reminder should fire.</param>
/// <param name="Precision">What the SOURCE actually specified. Drives the citation chip's wording.</param>
/// <param name="Confidence">
/// Confidence in the RESULT, not the source. <c>dateOnly</c> stays <c>high</c>
/// because nothing was guessed — the date is the source's and the time is the
/// user's. <c>floating</c> is <c>low</c> because the wall clock could genuinely
/// belong to any zone and picking one is a coin flip with a four-hour blast radius.
/// </param>
/// <param name="Note">Human-readable provenance. Never rendered without a source label in front of it.</param>
/// <param name="NeedsConfirmation">True when the caller MUST ask rather than silently scheduling.</param>
public readonly record struct ResolvedDueAt(
    DateTime DueAt,
    string Precision,
    string Confidence,
    string Note,
    bool NeedsConfirmation);

/// <summary>
/// Port of <c>server/src/modules/integrations/dateOnly.ts</c> — the single policy
/// for "an external source gave us a date but not a time".
///
/// <para>
/// Three of the four importable sources drop the time of day, each differently:
/// Google Tasks discards it by documented design, Microsoft To Do truncates to
/// midnight AND converts to UTC (so an Apr 15 task can arrive as
/// <c>2026-04-14T22:00:00</c> and fire on the WRONG DAY), Apple EKReminders are
/// date-only unless the user set hour/minute, and ICS feeds may be "floating".
/// Task requires a reminder to carry a real <c>dueAt</c>, so every one of those has
/// to become a UTC instant somewhere — and done ad hoc at four call sites it will
/// be four different wrong answers.
/// </para>
///
/// <para>
/// <b>THE POLICY, in one line: we never invent a time.</b> We either use the time
/// the source gave us, or we apply a time the USER stated, and we say which in the
/// provenance so the citation chip can show it. "09:00 because you told us to
/// nudge imported reminders at 09:00" is a fact about the user. "09:00 because the
/// model thought a dentist appointment sounds like a morning thing" is a guess
/// rendered as a promise.
/// </para>
/// </summary>
public static class ImportedTimeResolver
{
    /// <summary>
    /// Fallback when the user has never set one. Mirrors <c>DEFAULT_IMPORT_TIME</c>.
    /// </summary>
    public const string DefaultImportTime = "09:00";

    /// <summary>
    /// 'HH:mm' on a 24-hour clock and nothing else. Deliberately strict: a
    /// malformed preference silently becoming midnight would fire reminders in the
    /// middle of the night, which users do not report — they just turn
    /// notifications off.
    /// </summary>
    private static readonly Regex TimeOfDayPattern =
        new(@"^([01]\d|2[0-3]):([0-5]\d)$", RegexOptions.Compiled);

    /// <summary>Parse 'HH:mm', falling back to the default on anything odd.</summary>
    public static (int Hours, int Minutes) ParseTimeOfDay(string? value)
    {
        var match = TimeOfDayPattern.Match((value ?? string.Empty).Trim());
        if (!match.Success)
        {
            var parts = DefaultImportTime.Split(':');
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }

        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    /// <summary>
    /// Accept exactly the zone ids Node's <c>Intl</c> accepts — IANA only.
    ///
    /// <para><b>Resolving the id is not enough</b>, which is what this used to do.
    /// Measured against the reference via <c>PATCH /me</c>, where 400 means rejected:</para>
    ///
    /// <list type="table">
    ///   <item><term><c>Europe/London</c></term><description>Node 200 · resolves, HasIanaId=true</description></item>
    ///   <item><term><c>GMT Standard Time</c></term><description>Node <b>400</b> · resolves, HasIanaId=<b>false</b></description></item>
    ///   <item><term><c>Factory</c></term><description>Node <b>400</b> · resolves, HasIanaId=<b>true</b></description></item>
    ///   <item><term><c>Not/AZone</c></term><description>Node 400 · does not resolve</description></item>
    /// </list>
    ///
    /// <para>So it takes BOTH guards. <see cref="TimeZoneInfo.HasIanaId"/> rejects the
    /// Windows ids that .NET 6+ resolves on every platform — Outlook-published
    /// calendar feeds emit those constantly — and <c>Factory</c> is a tzdata
    /// placeholder that carries a real IANA id yet is absent from Intl's set.</para>
    ///
    /// <para><b>Why it is not cosmetic:</b> under the import rules a zone-less time
    /// is low-confidence and lands as <c>kind:'list'</c>, passive and never fired,
    /// while a resolved one becomes a <c>kind:'reminder'</c> that DOES fire. Accepting
    /// a zone Node rejects makes the port fire reminders the reference deliberately
    /// withholds.</para>
    /// </summary>
    public static bool IsValidTimeZone(string? timeZone)
    {
        if (string.IsNullOrEmpty(timeZone))
        {
            return false;
        }

        // tzdata ships this as a "no zone configured" placeholder. It has an IANA
        // id, so HasIanaId alone lets it through; Intl does not list it.
        if (string.Equals(timeZone, "Factory", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone).HasIanaId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Convert a LOCAL wall clock in <paramref name="timeZone"/> to the real UTC
    /// instant.
    ///
    /// <para>
    /// Ambiguous local times (the hour that repeats when clocks go back) resolve to
    /// the FIRST occurrence; nonexistent ones (the hour that is skipped) roll
    /// FORWARD. Both are the conventional choices and neither can be "right" —
    /// which is another reason the result carries provenance rather than
    /// pretending to precision.
    /// </para>
    /// </summary>
    public static DateTime ZonedWallClockToUtc(
        int year,
        int month,
        int day,
        int hours,
        int minutes,
        string timeZone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var wall = new DateTime(year, month, day, hours, minutes, 0, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(wall))
        {
            // Skipped hour — roll forward by the DST delta rather than throwing.
            var adjustment = zone.GetAdjustmentRules()
                .FirstOrDefault(r => wall >= r.DateStart && wall <= r.DateEnd)?
                .DaylightDelta ?? TimeSpan.FromHours(1);
            wall = wall.Add(adjustment);
        }

        if (zone.IsAmbiguousTime(wall))
        {
            // Repeated hour — take the FIRST occurrence, i.e. the larger (pre-fallback)
            // offset. GetAmbiguousTimeOffsets returns them ascending.
            var offsets = zone.GetAmbiguousTimeOffsets(wall);
            var first = offsets.Max();
            return DateTime.SpecifyKind(wall - first, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    /// <summary>
    /// A source gave us a bare date ('YYYY-MM-DD'). Apply the user's own default
    /// time in the user's own zone.
    ///
    /// <para>
    /// <b>Throws rather than defaulting to UTC when the timezone is missing.</b>
    /// That looks unhelpful and is deliberate: the profile's timezone is optional
    /// because on-device work can read the device clock, but an IMPORT runs with no
    /// device present. Guessing UTC for a user in Cairo moves every imported
    /// reminder two hours and nothing in the UI would ever reveal it. Callers must
    /// resolve the timezone first, or skip the item.
    /// </para>
    /// </summary>
    public static ResolvedDueAt ResolveDateOnly(string date, string timezone, string? defaultTimeOfDay = null)
    {
        if (!IsValidTimeZone(timezone))
        {
            throw new TimezoneRequiredException();
        }

        var (y, m, d) = SplitDate(date, $"Unparseable date-only value: {date}");
        var (hours, minutes) = ParseTimeOfDay(defaultTimeOfDay);
        var dueAt = ZonedWallClockToUtc(y, m, d, hours, minutes, timezone);
        var hhmm = $"{hours:D2}:{minutes:D2}";

        return new ResolvedDueAt(
            dueAt,
            "dateOnly",

            // Nothing was inferred. The date is the source's, the time is the user's.
            "high",
            $"date only — your {hhmm} default",
            false);
    }

    /// <summary>
    /// An ICS feed gave us a wall clock with no zone (<c>DTSTART:20260903T090000</c>,
    /// no trailing Z, no TZID). Per RFC 5545 that is a "floating" time meaning
    /// "09:00 wherever the observer happens to be".
    ///
    /// <para>
    /// We resolve it in the user's zone because that is the specified reading, but
    /// mark it LOW confidence and demand confirmation — the common real-world case
    /// is a publisher who meant a specific zone and omitted it. A London school
    /// publishing a floating 09:00 to a parent in Dubai silently moves the school
    /// run four hours.
    /// </para>
    /// </summary>
    public static ResolvedDueAt ResolveFloating(string date, string timeOfDay, string timezone)
    {
        if (!IsValidTimeZone(timezone))
        {
            throw new TimezoneRequiredException();
        }

        var (y, m, d) = SplitDate(date, $"Unparseable floating date: {date}");
        var (hours, minutes) = ParseTimeOfDay(timeOfDay);

        return new ResolvedDueAt(
            ZonedWallClockToUtc(y, m, d, hours, minutes, timezone),
            "floating",
            "low",
            "no timezone in source — assumed yours",
            true);
    }

    /// <summary>
    /// The source gave a real instant with a real zone. Nothing to decide; we record
    /// that fact so the citation can say so and the UI can tell it apart from the
    /// two cases above.
    /// </summary>
    public static ResolvedDueAt ResolveExact(DateTime instant) =>
        new(instant, "exact", "high", "time from source", false);

    private static (int Year, int Month, int Day) SplitDate(string date, string errorMessage)
    {
        var parts = date.Split('-');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var y) || y == 0
            || !int.TryParse(parts[1], out var m) || m == 0
            || !int.TryParse(parts[2], out var d) || d == 0)
        {
            throw new FormatException(errorMessage);
        }

        return (y, m, d);
    }
}
