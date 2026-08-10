using System.Globalization;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans.Binding;

/// <summary>The three <c>x-document-scan-*</c> headers, once validated.</summary>
public readonly record struct ScanMetadata(DateTime CapturedAt, string Source, string? Timezone);

/// <summary>
/// Port of the <c>HeadersSchema</c> zod object in <c>routes/me.documentScans.ts</c>.
///
/// <para>
/// The kernel's <c>HeaderReader</c> covers the ordinary cases, but this schema
/// needs two things it does not have: zod's <c>.datetime()</c> is far stricter
/// than <c>DateTime.TryParse</c> (RFC 3339 with a literal <c>Z</c>, nothing
/// else), and <c>timezone</c> carries a <c>.refine()</c> whose custom message
/// has to join the same issue list. So this mirrors the schema directly, using
/// the kernel's <c>ValidationIssue</c> / <c>ZodMessages</c> /
/// <c>ValidationDetails.AsFlattened</c> primitives for everything else.
/// </para>
///
/// <para>
/// Issues accumulate in SCHEMA order — capturedAt, source, timezone — and are
/// thrown together, because zod reports a whole object's failures in one
/// response rather than stopping at the first.
/// </para>
/// </summary>
public static class ScanMetadataBinder
{
    public const string CapturedAtHeader = "x-document-scan-captured-at";
    public const string SourceHeader = "x-document-scan-source";
    public const string TimezoneHeader = "x-document-scan-timezone";

    /// <summary>The route's own code and message. Copied verbatim.</summary>
    public const string InvalidCode = "invalid_metadata";

    public const string InvalidMessage = "Missing or invalid x-document-scan-* headers.";

    /// <summary>zod's <c>.refine()</c> message, which replaces the default entirely.</summary>
    public const string InvalidTimezoneMessage = "must be a valid IANA timezone";

    private const int TimezoneMinLength = 1;
    private const int TimezoneMaxLength = 64;

    /// <summary>
    /// zod's <c>z.string().datetime()</c> with default options: no offset form, any
    /// fractional precision, and a mandatory <c>Z</c>.
    /// </summary>
    private static readonly Regex ZodDateTime =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled);

    public static ScanMetadata Read(HttpRequest request)
    {
        var issues = new List<ValidationIssue>();

        var capturedAt = ReadCapturedAt(request, issues);
        var source = ReadSource(request, issues);
        var timezone = ReadTimezone(request, issues);

        if (issues.Count > 0)
        {
            throw AppException.BadRequest(InvalidCode, InvalidMessage, ValidationDetails.AsFlattened(issues));
        }

        return new ScanMetadata(capturedAt, source, timezone);
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
    /// Absent falls back to <c>pdf</c>; an unrecognised value is a HARD failure, not
    /// a silent fallback — a scan mislabelled as a PDF when it came from the camera
    /// would quietly change how it is processed.
    /// </summary>
    private static string ReadSource(HttpRequest request, List<ValidationIssue> issues)
    {
        if (!TryHeader(request, SourceHeader, out var raw))
        {
            return "pdf";
        }

        if (!ScannedDocumentVocabulary.Sources.Contains(raw, StringComparer.Ordinal))
        {
            issues.Add(ValidationIssue.At(
                "source",
                ZodMessages.InvalidEnum(ScannedDocumentVocabulary.Sources, raw)));
            return "pdf";
        }

        return raw;
    }

    private static string? ReadTimezone(HttpRequest request, List<ValidationIssue> issues)
    {
        if (!TryHeader(request, TimezoneHeader, out var raw))
        {
            return null;
        }

        // `.min(1)` runs before `.max(64)` runs before `.refine(...)`, and zod stops
        // a string chain at the first failing check.
        if (raw.Length < TimezoneMinLength)
        {
            issues.Add(ValidationIssue.At("timezone", ZodMessages.TooShort(TimezoneMinLength)));
            return null;
        }

        if (raw.Length > TimezoneMaxLength)
        {
            issues.Add(ValidationIssue.At("timezone", ZodMessages.TooLong(TimezoneMaxLength)));
            return null;
        }

        if (!IsValidIanaTimeZone(raw))
        {
            issues.Add(ValidationIssue.At("timezone", InvalidTimezoneMessage));
            return null;
        }

        return raw;
    }

    /// <summary>
    /// Node probes with <c>new Intl.DateTimeFormat('en-US', { timeZone: tz })</c>
    /// and treats a throw as invalid. <c>FindSystemTimeZoneById</c> resolves the
    /// same IANA database through ICU, so the two agree on real zone ids.
    /// </summary>
    public static bool IsValidIanaTimeZone(string timezone)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>false</c> only when the header is ABSENT. A header that is present but
    /// empty yields <c>""</c>, which zod sees as a string that fails
    /// <c>.min(1)</c> — a different outcome from <c>undefined</c>, which
    /// <c>.optional()</c> and <c>.default()</c> both accept.
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
