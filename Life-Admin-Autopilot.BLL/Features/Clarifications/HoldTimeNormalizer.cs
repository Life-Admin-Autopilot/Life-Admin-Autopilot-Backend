using System.Globalization;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// Port of <c>server/src/modules/ai/timeNormalize.ts</c> — the date lane every
/// tool argument goes through before it is stored.
///
/// <para>
/// <b>Why the API normalises at all.</b> The model routinely emits a naive
/// <c>2026-08-12T09:00:00</c> with no offset, or an offset it guessed. The server is
/// the source of truth for what "9am tomorrow" means, not the prompt — so a naive
/// wall clock is interpreted in the CALLER's zone, and only an explicit offset is
/// taken at face value.
/// </para>
///
/// <para>
/// This is deliberately NOT <c>BodyFields.IsoDate</c>. That reader mirrors zod's
/// <c>.datetime()</c>, which parses an offset-less string as UTC — right for
/// <c>POST /me/tasks</c>, where the client is our own frontend and always sends one,
/// and three hours wrong for a model in Cairo that sent none.
/// </para>
/// </summary>
public static class HoldTimeNormalizer
{
    /// <summary>
    /// <c>STRICT_DATETIME_RE</c>. Accepts naive, <c>Z</c>, and <c>±HH:MM</c> forms;
    /// rejects space-separated, date-only, and anything looser.
    ///
    /// <para>
    /// Anchored with <c>\A</c>/<c>\z</c> rather than <c>^</c>/<c>$</c>: .NET's
    /// <c>$</c> also matches before a trailing newline, so <c>"2026-08-12T09:00\n"</c>
    /// would pass a rule JavaScript rejects.
    /// </para>
    /// </summary>
    private static readonly Regex Strict = new(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The naive form, decomposed. Mirrors the second regex in the source.</summary>
    private static readonly Regex NaiveParts = new(
        @"\A(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Offset = new(
        @"(Z|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary><c>STRICT_DATETIME_RE.test(iso)</c>.</summary>
    public static bool IsStrictIso(string? iso) => iso is not null && Strict.IsMatch(iso);

    /// <summary><c>hasOffset(iso)</c>.</summary>
    public static bool HasOffset(string iso) => Offset.IsMatch(iso);

    /// <summary>
    /// The absolute instant a tool argument denotes.
    /// </summary>
    /// <param name="iso">Already known to satisfy <see cref="IsStrictIso"/>.</param>
    /// <param name="timezone">
    /// The caller's IANA zone. Absent or unrecognised falls back to reading the naive
    /// value as UTC — what <c>normalizeLocalIso</c> does with no zone, and
    /// deliberately not the host's local time, which would make the result depend on
    /// where the server happens to run.
    /// </param>
    public static DateTime Normalize(string iso, string? timezone)
    {
        if (!IsStrictIso(iso))
        {
            throw new ArgumentException($"invalid ISO 8601: {iso}", nameof(iso));
        }

        if (HasOffset(iso))
        {
            // Unambiguous — the offset says everything.
            return DateTime.SpecifyKind(
                DateTime.Parse(
                    iso,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTimeKind.Utc);
        }

        var parts = NaiveParts.Match(iso);

        // `Date.UTC(y, m, d, h, mi, s)` — note the fractional group is captured and
        // then NOT passed, so sub-second precision on a naive value is dropped. That
        // is the reference's behaviour, not an oversight of this port.
        var guess = new DateTime(
            int.Parse(parts.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(parts.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(parts.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(parts.Groups[4].Value, CultureInfo.InvariantCulture),
            int.Parse(parts.Groups[5].Value, CultureInfo.InvariantCulture),
            parts.Groups[6].Success ? int.Parse(parts.Groups[6].Value, CultureInfo.InvariantCulture) : 0,
            DateTimeKind.Utc);

        if (!ImportedTimeResolver.IsValidTimeZone(timezone))
        {
            return guess;
        }

        // The Intl round-trip, one-for-one: ask the zone what offset it applies at
        // `guess`, then shift the wall clock back by it. `GetUtcOffset` on a
        // Kind=Utc instant answers exactly what `timeZoneName: 'longOffset'` prints.
        var offset = TimeZoneInfo.FindSystemTimeZoneById(timezone!).GetUtcOffset(guess);
        return guess - offset;
    }
}
