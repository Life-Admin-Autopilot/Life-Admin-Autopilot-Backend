using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes.Binding;

/// <summary>
/// Port of <c>ExtractBodySchema</c> in <c>routes/me.voiceNotes.ts</c>:
/// <c>{ timezone?: string }</c>, trimmed, 1..64 chars, and a real IANA zone.
///
/// <para>
/// <b>Why the body is validated before the note is even looked up.</b> Node says
/// it in the route: a bad timezone used to crash extraction with a
/// <c>RangeError</c> deep inside <c>Intl.DateTimeFormat</c> and surface as a 500.
/// Validating at the boundary turns that into a friendly 400 — so the ORDER here
/// is part of the contract, not a style choice. Contrast the review route, which
/// looks the note up FIRST.
/// </para>
/// </summary>
public static class VoiceExtractBinder
{
    public const string InvalidCode = "invalid_body";
    public const string InvalidMessage = "Invalid extract payload.";

    /// <summary>zod's <c>.refine()</c> message, which replaces the default entirely.</summary>
    public const string InvalidTimezoneMessage = "must be a valid IANA timezone";

    private const int TimezoneMinLength = 1;
    private const int TimezoneMaxLength = 64;

    public static async Task<string?> ReadTimezoneAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        // A hand-read body does NOT inherit the Content-Type gate that
        // KernelBody.ReadAsync<T> applies. express.json() only parses
        // `application/json`, so anything else leaves `req.body` as `{}` and this
        // schema then succeeds with no timezone — and, crucially, a malformed or
        // oversized body is a 500 as JSON but a silent `{}` as text/plain.
        if (!KernelBody.IsJsonContentType(context.Request))
        {
            return null;
        }

        var bytes = await KernelBody
            .ReadBytesAsync(context.Request, KernelJson.MaxJsonBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            // `req.body ?? {}` — express sets `{}` for an empty body.
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            // Same failure mode as express.json(): malformed is a 500, not a 400.
            throw new BodyReadException("malformed JSON body", ex);
        }

        using (document)
        {
            return Parse(document.RootElement);
        }
    }

    private static string? Parse(JsonElement root)
    {
        var issues = new List<ValidationIssue>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add(ValidationIssue.Form(ZodMessages.ExpectedType("object", KindName(root.ValueKind))));
            throw Invalid(issues);
        }

        // Lenient: no `.strict()` on this schema, so unknown keys are stripped
        // rather than rejected.
        if (!root.TryGetProperty("timezone", out var node) || node.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            // `.optional()` accepts `undefined` only — a JSON null reaches the type
            // check and fails there.
            issues.Add(ValidationIssue.At("timezone", ZodMessages.ExpectedType("string", KindName(node.ValueKind))));
            throw Invalid(issues);
        }

        // `.trim()` is a zod string CHECK, so it mutates the value before `.min(1)`
        // and `.max(64)` see it: a timezone of spaces fails the minimum.
        var value = (node.GetString() ?? string.Empty).Trim();

        if (value.Length < TimezoneMinLength)
        {
            issues.Add(ValidationIssue.At("timezone", ZodMessages.TooShort(TimezoneMinLength)));
        }
        else if (value.Length > TimezoneMaxLength)
        {
            issues.Add(ValidationIssue.At("timezone", ZodMessages.TooLong(TimezoneMaxLength)));
        }

        // The refinement runs even when a length check already failed: ZodEffects
        // only skips it on an ABORTED inner parse, and a failed string check leaves
        // the parse merely dirty. So an over-long non-zone reports BOTH messages.
        if (!IsValidIanaTimeZone(value))
        {
            issues.Add(ValidationIssue.At("timezone", InvalidTimezoneMessage));
        }

        if (issues.Count > 0)
        {
            throw Invalid(issues);
        }

        return value;
    }

    /// <summary>
    /// Node probes with <c>new Intl.DateTimeFormat('en-US', { timeZone: tz })</c>
    /// and treats a throw as invalid. <c>FindSystemTimeZoneById</c> resolves the
    /// same IANA database through ICU, so the two agree on real zone ids.
    /// </summary>
    public static bool IsValidIanaTimeZone(string timezone)
    {
        if (timezone.Length == 0)
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return false;
        }
    }

    private static AppException Invalid(IEnumerable<ValidationIssue> issues) =>
        AppException.BadRequest(InvalidCode, InvalidMessage, ValidationDetails.AsFlattened(issues));

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        _ => "unknown",
    };
}
