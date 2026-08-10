using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes.Binding;

/// <summary>One accepted item as the caller described it. Every override is optional.</summary>
public sealed record VoiceReviewAccept(
    string Key,
    string? Title = null,
    string? Domain = null,
    string? Priority = null,
    DateTime? DueAt = null,
    string? Notes = null);

/// <summary>A parsed review payload. Both members default to empty.</summary>
public readonly record struct VoiceReviewBody(
    IReadOnlyList<VoiceReviewAccept> Accepts,
    IReadOnlyList<string> Discards);

/// <summary>
/// Port of <c>ReviewBodySchema</c> in <c>routes/me.voiceNotes.ts</c>.
///
/// <para>
/// Hand-walked rather than deserialized into a DTO because zod's
/// <c>flatten()</c> keys every nested issue under its TOP-LEVEL field: a bad
/// <c>accepts[3].key</c> is reported as <c>fieldErrors.accepts</c>, and a
/// round-trip through <c>JsonSerializer</c> loses the position information needed
/// to produce that faithfully.
/// </para>
///
/// <para>
/// The schema is LENIENT (no <c>.strict()</c>), so unknown keys — at the top level
/// and inside each accept — are silently stripped, exactly as a plain zod object
/// does.
/// </para>
///
/// <para>
/// <b>Deliberately not shared with <c>ScanReviewBinder</c></b>, which reads
/// identically today. They are two independent zod schemas in two Node route
/// files that happen to coincide; folding them together would mean a
/// document-scan-specific change silently altering the voice contract. The
/// duplication is the cheaper of the two risks.
/// </para>
/// </summary>
public static class VoiceReviewBinder
{
    public const string InvalidCode = "invalid_review";
    public const string InvalidMessage = "Invalid review payload.";

    private const int TitleMax = 240;
    private const int NotesMax = 2000;

    private static readonly Regex ZodDateTime =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled);

    private static readonly VoiceReviewBody Empty =
        new(Array.Empty<VoiceReviewAccept>(), Array.Empty<string>());

    /// <summary>
    /// PHASE ONE — the <c>express.json()</c> equivalent, and it must run BEFORE the
    /// note lookup.
    ///
    /// <para>
    /// This split is not tidiness, it is a measured parity fix. Node's body parser
    /// is global middleware: it has already run, and already thrown, by the time the
    /// route's first line executes. So <b>malformed JSON against an unknown note id
    /// is a 500, not a 404</b> — verified live against <c>:4200</c>. Folding the
    /// parse into phase two (which correctly runs after the lookup) made the
    /// candidate answer 404 there, which is exactly the shape of bug a
    /// hand-read body invites.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>null</c> when there is nothing to validate — a non-JSON content type or an
    /// empty body, both of which express turns into <c>req.body = {}</c>.
    /// </returns>
    public static async Task<JsonElement?> ReadRawAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        // The gate a hand-read body does not get for free. express.json() parses
        // ONLY `application/json`; every other type leaves `req.body` as `{}`, which
        // this schema accepts as "accept nothing, discard nothing". Skipping the
        // check would turn a text/plain body into a 400 (or a 500, if malformed)
        // that the reference server answers with a 200.
        if (!KernelBody.IsJsonContentType(context.Request))
        {
            return null;
        }

        // Same ceiling and same failure mode as express.json(): over 256kb or
        // malformed is a 500, not a 400.
        var bytes = await KernelBody
            .ReadBytesAsync(context.Request, KernelJson.MaxJsonBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);

            // Cloned so the element outlives the JsonDocument this method owns.
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new BodyReadException("malformed JSON body", ex);
        }
    }

    /// <summary>
    /// PHASE TWO — the route's own <c>ReviewBodySchema.safeParse(req.body ?? {})</c>,
    /// which runs AFTER the note lookup and produces <c>400 invalid_review</c>.
    /// </summary>
    public static VoiceReviewBody Parse(JsonElement? body)
    {
        if (body is not { } root)
        {
            return Empty;
        }

        var issues = new List<ValidationIssue>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add(ValidationIssue.Form(ZodMessages.ExpectedType("object", KindName(root.ValueKind))));
            throw Invalid(issues);
        }

        var accepts = ReadAccepts(root, issues);
        var discards = ReadDiscards(root, issues);

        if (issues.Count > 0)
        {
            throw Invalid(issues);
        }

        return new VoiceReviewBody(accepts, discards);
    }

    private static IReadOnlyList<VoiceReviewAccept> ReadAccepts(JsonElement root, List<ValidationIssue> issues)
    {
        if (!TryMember(root, "accepts", out var node))
        {
            return Array.Empty<VoiceReviewAccept>();
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Field("accepts", ZodMessages.ExpectedType("array", KindName(node.ValueKind))));
            return Array.Empty<VoiceReviewAccept>();
        }

        var accepts = new List<VoiceReviewAccept>();
        foreach (var element in node.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Field("accepts", ZodMessages.ExpectedType("object", KindName(element.ValueKind))));
                continue;
            }

            accepts.Add(new VoiceReviewAccept(
                Key: RequiredNonEmptyString(element, "key", "accepts", issues) ?? string.Empty,
                Title: OptionalTrimmedString(element, "title", "accepts", TitleMax, issues),
                Domain: OptionalEnum(element, "domain", "accepts", TaskVocabulary.Domains, issues),
                Priority: OptionalEnum(element, "priority", "accepts", TaskVocabulary.Priorities, issues),
                DueAt: OptionalDateTime(element, "dueAt", "accepts", issues),
                Notes: OptionalString(element, "notes", "accepts", NotesMax, issues)));
        }

        return accepts;
    }

    private static IReadOnlyList<string> ReadDiscards(JsonElement root, List<ValidationIssue> issues)
    {
        if (!TryMember(root, "discards", out var node))
        {
            return Array.Empty<string>();
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Field("discards", ZodMessages.ExpectedType("array", KindName(node.ValueKind))));
            return Array.Empty<string>();
        }

        var discards = new List<string>();
        foreach (var element in node.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                issues.Add(Field("discards", ZodMessages.ExpectedType("string", KindName(element.ValueKind))));
                continue;
            }

            var value = element.GetString() ?? string.Empty;
            if (value.Length == 0)
            {
                issues.Add(Field("discards", ZodMessages.TooShort(1)));
                continue;
            }

            discards.Add(value);
        }

        return discards;
    }

    private static string? RequiredNonEmptyString(
        JsonElement element,
        string name,
        string topLevelField,
        List<ValidationIssue> issues)
    {
        if (!TryMember(element, name, out var node))
        {
            issues.Add(Field(topLevelField, ZodMessages.Required));
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            issues.Add(Field(topLevelField, ZodMessages.ExpectedType("string", KindName(node.ValueKind))));
            return null;
        }

        var value = node.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            issues.Add(Field(topLevelField, ZodMessages.TooShort(1)));
            return null;
        }

        return value;
    }

    /// <summary>
    /// <c>z.string().trim().min(1).max(240)</c> — the trim runs FIRST, so a title of
    /// spaces fails <c>.min(1)</c> rather than being stored as an empty string.
    /// </summary>
    private static string? OptionalTrimmedString(
        JsonElement element,
        string name,
        string topLevelField,
        int max,
        List<ValidationIssue> issues)
    {
        if (!TryMember(element, name, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            issues.Add(Field(topLevelField, ZodMessages.ExpectedType("string", KindName(node.ValueKind))));
            return null;
        }

        var value = (node.GetString() ?? string.Empty).Trim();
        if (value.Length < 1)
        {
            issues.Add(Field(topLevelField, ZodMessages.TooShort(1)));
            return null;
        }

        if (value.Length > max)
        {
            issues.Add(Field(topLevelField, ZodMessages.TooLong(max)));
            return null;
        }

        return value;
    }

    private static string? OptionalString(
        JsonElement element,
        string name,
        string topLevelField,
        int max,
        List<ValidationIssue> issues)
    {
        if (!TryMember(element, name, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            issues.Add(Field(topLevelField, ZodMessages.ExpectedType("string", KindName(node.ValueKind))));
            return null;
        }

        var value = node.GetString() ?? string.Empty;
        if (value.Length > max)
        {
            issues.Add(Field(topLevelField, ZodMessages.TooLong(max)));
            return null;
        }

        return value;
    }

    private static string? OptionalEnum(
        JsonElement element,
        string name,
        string topLevelField,
        IReadOnlyList<string> allowed,
        List<ValidationIssue> issues)
    {
        if (!TryMember(element, name, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            issues.Add(Field(topLevelField, ZodMessages.InvalidEnum(allowed, KindName(node.ValueKind))));
            return null;
        }

        var value = node.GetString() ?? string.Empty;
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            issues.Add(Field(topLevelField, ZodMessages.InvalidEnum(allowed, value)));
            return null;
        }

        return value;
    }

    private static DateTime? OptionalDateTime(
        JsonElement element,
        string name,
        string topLevelField,
        List<ValidationIssue> issues)
    {
        if (!TryMember(element, name, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            issues.Add(Field(topLevelField, ZodMessages.ExpectedType("string", KindName(node.ValueKind))));
            return null;
        }

        var raw = node.GetString() ?? string.Empty;
        if (!ZodDateTime.IsMatch(raw) ||
            !DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            issues.Add(Field(topLevelField, ZodMessages.InvalidDatetime));
            return null;
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>
    /// A JSON <c>null</c> is NOT the same as an absent key: zod's <c>.optional()</c>
    /// accepts <c>undefined</c> only, so a null value has to reach the type check
    /// and fail there.
    /// </summary>
    private static bool TryMember(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Undefined;

    private static ValidationIssue Field(string topLevelField, string message) =>
        ValidationIssue.At(topLevelField, message);

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
