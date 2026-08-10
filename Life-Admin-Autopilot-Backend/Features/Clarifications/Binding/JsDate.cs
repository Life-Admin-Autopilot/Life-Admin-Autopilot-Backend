using System.Globalization;
using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot_Backend.Features.Clarifications.Binding;

/// <summary>
/// <c>new Date(string)</c>, for the one place a raw query value is fed straight to
/// it: the list route's <c>before</c> cursor.
///
/// <para>
/// This is NOT <c>zod.datetime()</c> and must not be confused with it.
/// <c>BodyFields.IsoDate</c> mirrors the strict schema lane, where anything but a
/// full ISO instant is a 400. Here there is no schema at all — the route calls the
/// JS constructor and simply DROPS an unparsable value, so the failure mode is a
/// silently-ignored cursor, never an error.
/// </para>
///
/// <para>
/// The one rule worth spelling out is the offset default, because the two lanes
/// disagree. ECMA-262 parses a date-only form (<c>2026-01-15</c>) as UTC but a
/// date-TIME with no offset (<c>2026-01-15T10:00:00</c>) as LOCAL time. The strict
/// lane assumes UTC for both. Getting this wrong shifts a cursor by the host's
/// offset and silently drops or duplicates a page boundary.
/// </para>
/// </summary>
public static partial class JsDate
{
    /// <summary>The ECMA-262 Date Time String Format, with every optional part optional.</summary>
    [GeneratedRegex(
        @"^[+-]?\d{4,6}-\d{2}(-\d{2})?([T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoLike();

    /// <summary>True when the value parses; false is the <c>NaN</c> the route ignores.</summary>
    public static bool TryParse(string? raw, out DateTime value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!IsoLike().IsMatch(raw))
        {
            return false;
        }

        // A date-only form is UTC midnight; a naive date-time is host-local. Anything
        // carrying Z or an explicit offset parses unambiguously either way.
        var hasTime = raw.Contains('T', StringComparison.Ordinal) || raw.Contains(' ', StringComparison.Ordinal);
        var hasOffset = HasOffset(raw);

        var styles = hasTime && !hasOffset
            ? DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal
            : DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return false;
        }

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    private static bool HasOffset(string raw)
    {
        if (raw.EndsWith('Z') || raw.EndsWith('z'))
        {
            return true;
        }

        // Only a trailing sign counts — the leading one belongs to an expanded year.
        var timeStart = raw.IndexOfAny(new[] { 'T', ' ' });
        if (timeStart < 0)
        {
            return false;
        }

        var time = raw[timeStart..];
        return time.Contains('+', StringComparison.Ordinal) || time.Contains('-', StringComparison.Ordinal);
    }
}
