using System.Globalization;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <summary>The wall-clock components as the feed wrote them, before any conversion.</summary>
public readonly record struct IcsWallClock(int Year, int Month, int Day, int Hours, int Minutes)
{
    public string IsoDate => $"{Year:D4}-{Month:D2}-{Day:D2}";

    /// <summary>
    /// <c>YYYYMMDDTHHMM</c> — the occurrence identity used for <c>externalId</c> and
    /// for matching a RECURRENCE-ID to the slot it replaces. Seconds are deliberately
    /// absent, mirroring the reference's <c>stamp()</c>.
    /// </summary>
    public string Stamp => $"{Year:D4}{Month:D2}{Day:D2}T{Hours:D2}{Minutes:D2}";
}

public enum IcsZoneKind
{
    /// <summary>All-day: the feed stated a date and nothing else.</summary>
    Date,

    /// <summary>Explicit UTC — a trailing Z.</summary>
    Utc,

    /// <summary>A TZID we could actually resolve.</summary>
    Zoned,

    /// <summary>No zone, or a TZID we could not resolve.</summary>
    Floating,
}

/// <summary>
/// What the feed said about this time's zone.
///
/// <para>
/// <see cref="DeclaredTzid"/> is set when the feed named a zone that could not be
/// resolved — an Outlook feed emitting <c>TZID="GMT Standard Time"</c>, a Windows
/// identifier rather than an IANA one. The name is kept so the user can be told
/// what was intended, even though it cannot be honoured.
/// </para>
/// </summary>
public readonly record struct IcsZoneSpec(IcsZoneKind Kind, string? Tzid = null, string? DeclaredTzid = null);

/// <param name="RawLine">
/// The original DTSTART line, verbatim. For a low-confidence floating time the raw
/// line IS the evidence, and paraphrasing it would defeat the point.
/// </param>
public readonly record struct IcsTimeResolution(ResolvedDueAt When, string RawLine)
{
    public DateTime DueAt => When.DueAt;

    public string Precision => When.Precision;

    public string Confidence => When.Confidence;

    public bool NeedsConfirmation => When.NeedsConfirmation;
}

/// <param name="DefaultTimeOfDay">The user's 'HH:mm' import default, for all-day events.</param>
public readonly record struct IcsTimeContext(string Timezone, string? DefaultTimeOfDay);

/// <summary>
/// Port of <c>server/src/modules/integrations/ics/resolveIcsTime.ts</c> — turn an
/// ICS DTSTART into a firing instant, honestly.
///
/// <para>
/// This module exists because a calendar library's "give me the instant" API cannot
/// be trusted for feed data, and the way it fails is silent. Given
/// <c>DTSTART;TZID=Europe/London:20260903T090000</c> in a feed carrying no
/// VTIMEZONE block — extremely common, because publishers assume the consumer knows
/// what Europe/London means — a parser finds no registered zone, falls back to
/// "floating", and resolves the wall clock against the SERVER's local timezone. On
/// a UTC box that is a one-hour error; on a machine set to UTC-4 it is five.
/// Nothing in the return value says so. The event just lands at the wrong time
/// forever.
/// </para>
///
/// <para>
/// So the resolved instant is never taken from the parser. The raw wall-clock
/// components and the TZID <i>parameter</i> are read directly (the parameter
/// survives even when the zone itself is unresolvable), the case is classified, and
/// the conversion is handed to the kernel's <see cref="ImportedTimeResolver"/>.
/// </para>
///
/// <para>
/// Classification (<see cref="ClassifyZone"/>) is split from resolution
/// (<see cref="ResolveWallClock"/>) because recurrence expansion needs both halves
/// separately: an RRULE occurrence inherits its master's zone, but each occurrence
/// must be converted on its own. Converting once and adding seven days repeatedly
/// drifts by an hour the moment a series crosses a DST boundary — which a weekly
/// school event does twice a year.
/// </para>
/// </summary>
public static class IcsTimeResolver
{
    /// <summary>
    /// Read the wall clock a DATE / DATE-TIME value states. Returns null when the
    /// value is not one of the three RFC 5545 forms, so the caller can skip the
    /// event rather than inventing a time.
    /// </summary>
    public static (IcsWallClock Wall, bool IsDate, bool IsUtc)? ReadValue(IcsProperty property)
    {
        var value = property.Value.Trim();
        var forcedDate = string.Equals(property.Parameter("value"), "DATE", StringComparison.OrdinalIgnoreCase);

        if (value.Length == 8 || forcedDate)
        {
            var datePart = value.Length >= 8 ? value[..8] : value;
            return TryDate(datePart, out var day)
                ? (new IcsWallClock(day.Year, day.Month, day.Day, 0, 0), true, false)
                : null;
        }

        var isUtc = value.EndsWith('Z');
        var body = isUtc ? value[..^1] : value;

        // YYYYMMDDTHHMMSS — seconds are read for shape validation and then dropped,
        // matching the reference, which resolves from hour and minute only.
        if (body.Length != 15 || body[8] != 'T')
        {
            return null;
        }

        if (!TryDate(body[..8], out var date)
            || !int.TryParse(body.AsSpan(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(body.AsSpan(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(body.AsSpan(13, 2), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || hours > 23
            || minutes > 59)
        {
            return null;
        }

        return (new IcsWallClock(date.Year, date.Month, date.Day, hours, minutes), false, isUtc);
    }

    /// <summary>
    /// Decide which of the four cases a DTSTART property falls into.
    ///
    /// <para>
    /// Reads the TZID <b>parameter</b> rather than any resolved zone object, because
    /// a parser reports "floating" for any zone the feed did not also define as a
    /// VTIMEZONE — which collapses the <c>Zoned</c> and <c>Floating</c> cases
    /// together and loses the only evidence that a zone was ever specified.
    /// </para>
    /// </summary>
    public static IcsZoneSpec ClassifyZone(IcsProperty property, bool isDate, bool isUtc)
    {
        if (isDate)
        {
            return new IcsZoneSpec(IcsZoneKind.Date);
        }

        if (isUtc)
        {
            return new IcsZoneSpec(IcsZoneKind.Utc);
        }

        var tzid = property.Parameter("tzid");
        if (!string.IsNullOrEmpty(tzid) && IsIanaTimeZone(tzid))
        {
            return new IcsZoneSpec(IcsZoneKind.Zoned, tzid);
        }

        return string.IsNullOrEmpty(tzid)
            ? new IcsZoneSpec(IcsZoneKind.Floating)
            : new IcsZoneSpec(IcsZoneKind.Floating, DeclaredTzid: tzid);
    }

    /// <summary>
    /// Is this a real IANA zone name?
    ///
    /// <para>
    /// <b>Not <see cref="ImportedTimeResolver.IsValidTimeZone"/>, and the difference
    /// is a parity bug waiting to happen.</b> That kernel helper asks
    /// <c>TimeZoneInfo.FindSystemTimeZoneById</c>, which on .NET 6+ ALSO accepts
    /// Windows identifiers — <c>"GMT Standard Time"</c> resolves happily on macOS and
    /// Linux. The reference asks <c>Intl.DateTimeFormat</c>, which does not.
    /// </para>
    ///
    /// <para>
    /// A TZID is publisher-controlled and Outlook feeds emit Windows ids constantly,
    /// so taking the kernel helper here would classify an Outlook feed as
    /// <c>zoned/exact/high</c> where the reference says
    /// <c>floating/low/needsConfirmation</c> — the difference between a matter that
    /// FIRES and one that waits to be confirmed. <c>HasIanaId</c> is false for exactly
    /// the ids the reference rejects.
    /// </para>
    /// </summary>
    public static bool IsIanaTimeZone(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id).HasIanaId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve one wall clock under a known zone spec.
    ///
    /// <para>
    /// Safe to call per-occurrence during recurrence expansion: every call converts
    /// independently, so a series crossing a DST boundary lands correctly on both
    /// sides of it.
    /// </para>
    /// </summary>
    public static IcsTimeResolution ResolveWallClock(
        IcsWallClock wall,
        IcsZoneSpec spec,
        IcsTimeContext context,
        string rawLine)
    {
        // All-day. The feed stated a date and nothing else, so this is the same
        // situation as a Google Task — apply the user's own default time.
        if (spec.Kind == IcsZoneKind.Date)
        {
            return new IcsTimeResolution(
                ImportedTimeResolver.ResolveDateOnly(wall.IsoDate, context.Timezone, context.DefaultTimeOfDay),
                rawLine);
        }

        // Explicit UTC. The components already ARE UTC, so there is nothing to
        // convert and no ambiguity to flag.
        if (spec.Kind == IcsZoneKind.Utc)
        {
            return new IcsTimeResolution(
                new ResolvedDueAt(
                    new DateTime(wall.Year, wall.Month, wall.Day, wall.Hours, wall.Minutes, 0, DateTimeKind.Utc),
                    "exact",
                    "high",
                    "time from source (UTC)",
                    false),
                rawLine);
        }

        if (spec.Kind == IcsZoneKind.Zoned)
        {
            return new IcsTimeResolution(
                new ResolvedDueAt(
                    ImportedTimeResolver.ZonedWallClockToUtc(
                        wall.Year, wall.Month, wall.Day, wall.Hours, wall.Minutes, spec.Tzid!),
                    "exact",
                    "high",
                    $"time from source ({spec.Tzid})",
                    false),
                rawLine);
        }

        // Floating, or a TZID we could not resolve. RFC 5545 says a floating time
        // means "whatever the observer's clock says", so the user's zone is the
        // specified reading — but publishers routinely mean a specific zone and omit
        // it, and a London school feed read by a Dubai parent moves the school run
        // four hours. Low confidence, ask before scheduling.
        if (!ImportedTimeResolver.IsValidTimeZone(context.Timezone))
        {
            // Mirrors the date-only policy's refusal to guess: no device is present
            // during a feed poll, so there is nothing to fall back to. Delegating
            // lets that module own the single "we cannot proceed without a
            // timezone" behaviour — it throws.
            return new IcsTimeResolution(
                ImportedTimeResolver.ResolveDateOnly(wall.IsoDate, context.Timezone, context.DefaultTimeOfDay),
                rawLine);
        }

        return new IcsTimeResolution(
            new ResolvedDueAt(
                ImportedTimeResolver.ZonedWallClockToUtc(
                    wall.Year, wall.Month, wall.Day, wall.Hours, wall.Minutes, context.Timezone),
                "floating",
                "low",
                spec.DeclaredTzid is not null
                    ? $"unrecognised timezone \"{spec.DeclaredTzid}\" — assumed yours"
                    : "no timezone in source — assumed yours",
                true),
            rawLine);
    }

    private static bool TryDate(string yyyymmdd, out DateOnly date)
    {
        date = default;
        return yyyymmdd.Length == 8
            && DateOnly.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
