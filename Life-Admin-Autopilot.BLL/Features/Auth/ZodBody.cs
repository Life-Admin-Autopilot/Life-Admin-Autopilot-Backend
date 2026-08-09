using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Validation;

namespace Life_Admin_Autopilot.BLL.Features.Auth;

/// <summary>
/// A raw JSON body, read as its top-level members without any typed binding.
///
/// <para>
/// Deliberately untyped. Binding to a POCO first would collapse two cases zod
/// reports differently — an ABSENT key (<c>"Required"</c>) and a key holding the
/// wrong type (<c>"Expected string, received number"</c>) — and a type mismatch
/// would surface as a deserialization failure long before the route could
/// describe it. Every auth schema is lenient, so unknown members simply sit here
/// unread, which is exactly zod's strip behaviour.
/// </para>
/// </summary>
public sealed class RawJsonBody
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}

/// <summary>
/// A zod <c>z.object({...})</c>, reproduced closely enough that the emitted
/// issues match member for member.
///
/// <para>
/// <b>Call the readers in the order the Node schema declares its keys.</b> zod
/// walks the shape in declaration order and accumulates every issue rather than
/// stopping at the first, so both the SET and the ORDER of issues are observable
/// in <c>details</c> — as an array for the <c>.parse()</c> routes and as
/// insertion-ordered <c>fieldErrors</c> for the <c>safeParse</c> ones.
/// </para>
/// </summary>
public sealed class ZodBody
{
    private readonly Dictionary<string, JsonElement> _fields;
    private readonly List<ValidationIssue> _issues = new();

    public ZodBody(RawJsonBody body) => _fields = body.Fields;

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public bool IsValid => _issues.Count == 0;

    /// <summary>Adds a <c>.refine()</c>-style cross-field issue at an explicit path.</summary>
    public void AddIssue(string path, string message) =>
        _issues.Add(ValidationIssue.At(path, message));

    /// <summary><c>z.string()</c> with optional <c>.min()</c> / <c>.max()</c>. Required.</summary>
    public string? RequiredString(string key, int? min = null, int? max = null)
    {
        if (!TryReadString(key, required: true, out var value))
        {
            return null;
        }

        return CheckLength(key, value!, min, max);
    }

    /// <summary><c>z.string().optional()</c>. An absent key is not an issue.</summary>
    public string? OptionalString(string key, int? min = null, int? max = null)
    {
        if (!_fields.ContainsKey(key))
        {
            return null;
        }

        return !TryReadString(key, required: false, out var value)
            ? null
            : CheckLength(key, value!, min, max);
    }

    /// <summary>
    /// <c>z.string().email().toLowerCase().trim()</c>.
    ///
    /// <para>
    /// The format check runs BEFORE the transforms, so a padded address is
    /// rejected rather than trimmed into validity. Delegates to
    /// <c>NodeFieldRules.NormalizeEmail</c> so the regex lives in one place.
    /// </para>
    /// </summary>
    public string? Email(string key)
    {
        if (!TryReadString(key, required: true, out var value))
        {
            return null;
        }

        var normalized = NodeFieldRules.NormalizeEmail(value);
        if (normalized is null)
        {
            _issues.Add(ValidationIssue.At(key, ZodMessages.InvalidEmail));
            return null;
        }

        return normalized;
    }

    /// <summary>
    /// <c>z.string().trim().regex(/^\d{6}$/, 'Enter the 6-digit code.')</c>.
    ///
    /// <para>
    /// Trim runs BEFORE the regex here — the opposite order to
    /// <see cref="Email"/> — so <c>" 424242 "</c> is accepted and normalised.
    /// The failure message is the schema's own custom string, which is why it
    /// reads identically to the envelope message on that route.
    /// </para>
    /// </summary>
    public string? SixDigitCode(string key)
    {
        if (!TryReadString(key, required: true, out var value))
        {
            return null;
        }

        var normalized = NodeFieldRules.NormalizeSixDigitCode(value);
        if (normalized is null)
        {
            _issues.Add(ValidationIssue.At(key, CodeMessage));
            return null;
        }

        return normalized;
    }

    /// <summary>The custom regex message on both code schemas.</summary>
    public const string CodeMessage = "Enter the 6-digit code.";

    private bool TryReadString(string key, bool required, out string? value)
    {
        value = null;

        if (!_fields.TryGetValue(key, out var element))
        {
            if (required)
            {
                _issues.Add(ValidationIssue.At(key, ZodMessages.Required));
            }

            return false;
        }

        // zod treats an explicit `undefined` as absent, but JSON has no undefined —
        // an explicit null is a TYPE error, not a missing key.
        if (element.ValueKind != JsonValueKind.String)
        {
            _issues.Add(ValidationIssue.At(key, ZodMessages.ExpectedType("string", JsTypeName(element.ValueKind))));
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    private string? CheckLength(string key, string value, int? min, int? max)
    {
        // Length is measured in UTF-16 code units, matching JavaScript's String#length.
        if (min is { } lower && value.Length < lower)
        {
            _issues.Add(ValidationIssue.At(key, ZodMessages.TooShort(lower)));
            return null;
        }

        if (max is { } upper && value.Length > upper)
        {
            _issues.Add(ValidationIssue.At(key, ZodMessages.TooLong(upper)));
            return null;
        }

        return value;
    }

    /// <summary>The name zod's error map uses for a received value's type.</summary>
    private static string JsTypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "unknown",
    };
}
