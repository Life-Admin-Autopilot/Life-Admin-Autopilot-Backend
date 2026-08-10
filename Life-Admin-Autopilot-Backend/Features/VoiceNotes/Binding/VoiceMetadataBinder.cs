using System.Globalization;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes.Binding;

/// <summary>The four <c>x-voice-note-*</c> headers, once validated.</summary>
public readonly record struct VoiceMetadata(int DurationMs, DateTime CapturedAt, string Source, string? Timezone);

/// <summary>
/// Port of the <c>HeadersSchema</c> zod object in <c>routes/me.voiceNotes.ts</c>.
///
/// <para>
/// The kernel's <c>HeaderReader</c> covers the ordinary cases, but this schema
/// needs three things it does not have, and each one is load-bearing:
/// </para>
/// <list type="number">
///   <item>
///     <c>durationMs</c> is <c>z.coerce.number()</c>, so an ABSENT header does not
///     produce <c>"Required"</c> — it produces
///     <c>"Expected number, received nan"</c>, because <c>Number(undefined)</c> is
///     <c>NaN</c> and the coercion happens before the type check. The contract
///     calls this out explicitly. A present-but-EMPTY header is different again:
///     <c>Number("")</c> is <c>0</c>, which is a perfectly valid duration.
///   </item>
///   <item>
///     zod's <c>.datetime()</c> is far stricter than <c>DateTime.TryParse</c> —
///     RFC 3339 with a literal <c>Z</c>, nothing else.
///   </item>
///   <item>
///     <c>timezone</c> here is NOT IANA-validated. That is a real asymmetry with
///     the document-scan upload and with this slice's own <c>extract-tasks</c>
///     body, both of which DO validate it: on this route any 1..64 character
///     string is accepted and stored verbatim. Adding a check would reject uploads
///     the reference server accepts.
///   </item>
/// </list>
///
/// <para>
/// Issues accumulate in SCHEMA order — durationMs, capturedAt, source, timezone —
/// and are thrown together, because zod reports a whole object's failures in one
/// response rather than stopping at the first.
/// </para>
/// </summary>
public static class VoiceMetadataBinder
{
    public const string DurationHeader = "x-voice-note-duration-ms";
    public const string CapturedAtHeader = "x-voice-note-captured-at";
    public const string SourceHeader = "x-voice-note-source";
    public const string TimezoneHeader = "x-voice-note-timezone";

    /// <summary>The route's own code and message. Copied verbatim.</summary>
    public const string InvalidCode = "invalid_metadata";

    public const string InvalidMessage = "Missing or invalid x-voice-note-* headers.";

    /// <summary>
    /// zod's <c>z.string().datetime()</c> with default options: no offset form, any
    /// fractional precision, and a mandatory <c>Z</c>.
    /// </summary>
    private static readonly Regex ZodDateTime =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled);

    public static VoiceMetadata Read(HttpRequest request)
    {
        var issues = new List<ValidationIssue>();

        var durationMs = ReadDurationMs(request, issues);
        var capturedAt = ReadCapturedAt(request, issues);
        var source = ReadSource(request, issues);
        var timezone = ReadTimezone(request, issues);

        if (issues.Count > 0)
        {
            throw AppException.BadRequest(InvalidCode, InvalidMessage, ValidationDetails.AsFlattened(issues));
        }

        return new VoiceMetadata(durationMs, capturedAt, source, timezone);
    }

    /// <summary>
    /// <c>z.coerce.number().int().min(0).max(600000)</c>.
    ///
    /// <para>
    /// A failed COERCION aborts (zod returns <c>INVALID</c> from the type check), so
    /// a non-numeric header yields exactly one issue. The three CHECKS afterwards
    /// accumulate — zod's <c>ZodNumber</c> records every failing check rather than
    /// stopping — so <c>-1.5</c> reports both the integer failure and the minimum.
    /// </para>
    /// </summary>
    private static int ReadDurationMs(HttpRequest request, List<ValidationIssue> issues)
    {
        var coerced = JsNumber(TryHeader(request, DurationHeader, out var raw) ? raw : null);

        if (double.IsNaN(coerced))
        {
            issues.Add(ValidationIssue.At("durationMs", ZodMessages.ExpectedType("number", "nan")));
            return 0;
        }

        var valid = true;

        if (!IsJsInteger(coerced))
        {
            issues.Add(ValidationIssue.At("durationMs", ZodMessages.ExpectedType("integer", "float")));
            valid = false;
        }

        if (coerced < 0)
        {
            issues.Add(ValidationIssue.At("durationMs", ZodMessages.NumberTooSmall(0)));
            valid = false;
        }

        if (coerced > VoiceNoteVocabulary.MaxDurationMs)
        {
            issues.Add(ValidationIssue.At(
                "durationMs",
                ZodMessages.NumberTooBig(VoiceNoteVocabulary.MaxDurationMs)));
            valid = false;
        }

        return valid ? (int)coerced : 0;
    }

    private static DateTime ReadCapturedAt(HttpRequest request, List<ValidationIssue> issues)
    {
        if (!TryHeader(request, CapturedAtHeader, out var raw))
        {
            issues.Add(ValidationIssue.At("capturedAt", ZodMessages.Required));
            return default;
        }

        if (!ZodDateTime.IsMatch(raw) ||
            !DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            issues.Add(ValidationIssue.At("capturedAt", ZodMessages.InvalidDatetime));
            return default;
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>
    /// Absent falls back to <c>app</c>; an unrecognised value is a HARD failure, not
    /// a silent fallback — a note captured from the lock screen and relabelled as an
    /// in-app recording would quietly misattribute where the user was.
    /// </summary>
    private static string ReadSource(HttpRequest request, List<ValidationIssue> issues)
    {
        if (!TryHeader(request, SourceHeader, out var raw))
        {
            return "app";
        }

        if (!VoiceNoteVocabulary.Sources.Contains(raw, StringComparer.Ordinal))
        {
            issues.Add(ValidationIssue.At("source", ZodMessages.InvalidEnum(VoiceNoteVocabulary.Sources, raw)));
            return "app";
        }

        return raw;
    }

    /// <summary>Length only. See the class remarks — no IANA check on this route.</summary>
    private static string? ReadTimezone(HttpRequest request, List<ValidationIssue> issues)
    {
        if (!TryHeader(request, TimezoneHeader, out var raw))
        {
            return null;
        }

        if (raw.Length < VoiceNoteVocabulary.TimezoneMinLength)
        {
            issues.Add(ValidationIssue.At(
                "timezone",
                ZodMessages.TooShort(VoiceNoteVocabulary.TimezoneMinLength)));
            return null;
        }

        if (raw.Length > VoiceNoteVocabulary.TimezoneMaxLength)
        {
            issues.Add(ValidationIssue.At(
                "timezone",
                ZodMessages.TooLong(VoiceNoteVocabulary.TimezoneMaxLength)));
            return null;
        }

        return raw;
    }

    /// <summary>
    /// JavaScript's <c>Number(value)</c>, which is what <c>z.coerce.number()</c>
    /// calls. The surprises worth reproducing: <c>undefined</c> and unparseable text
    /// are <c>NaN</c>, but the empty string and whitespace are <c>0</c>, and the
    /// radix prefixes and <c>Infinity</c> all parse.
    /// </summary>
    internal static double JsNumber(string? value)
    {
        if (value is null)
        {
            return double.NaN;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        var radix = trimmed.Length > 2 && trimmed[0] == '0'
            ? char.ToLowerInvariant(trimmed[1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 0,
            }
            : 0;

        if (radix != 0)
        {
            try
            {
                return Convert.ToInt64(trimmed[2..], radix);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                return double.NaN;
            }
        }

        return double.TryParse(
            trimmed,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : double.NaN;
    }

    /// <summary><c>Number.isInteger</c> — false for infinities, unlike a naive modulo test.</summary>
    private static bool IsJsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    /// <summary>
    /// <c>false</c> only when the header is ABSENT. A header that is present but
    /// empty yields <c>""</c>, and for these four schemas that is a genuinely
    /// different outcome from <c>undefined</c> every time: <c>""</c> coerces to
    /// <c>0</c> for the duration, fails <c>.datetime()</c> rather than
    /// <c>Required</c>, fails the source enum rather than defaulting, and fails
    /// <c>.min(1)</c> rather than being optional.
    /// </summary>
    private static bool TryHeader(HttpRequest request, string name, out string value)
    {
        if (!request.Headers.TryGetValue(name, out var values))
        {
            value = string.Empty;
            return false;
        }

        value = values.ToString();
        return true;
    }
}
