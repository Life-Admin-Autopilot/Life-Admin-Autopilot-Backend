using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.Clarifications.Binding;

/// <summary>
/// The raw body of <c>POST /me/clarifications/{id}/resolve</c>.
///
/// <para>
/// Both members are <see cref="JsonElement"/> so absent and present-but-wrong-typed
/// stay distinguishable — <c>answer</c> is a discriminated union and needs its own
/// walk, and a nullable CLR property would collapse "missing" into "null".
/// </para>
///
/// <para>
/// The Node schema is NOT <c>.strict()</c>: unknown top-level keys are stripped in
/// silence, never rejected.
/// </para>
/// </summary>
public sealed class ResolveBody
{
    [JsonPropertyName("answer")]
    public JsonElement Answer { get; init; }

    [JsonPropertyName("timezone")]
    public JsonElement Timezone { get; init; }
}

/// <summary>Which arm of the union the caller picked.</summary>
public enum ResolveAnswerKind
{
    /// <summary>A pre-resolved suggestion — deterministic, no AI.</summary>
    Option,

    /// <summary>Free text — needs one bounded model call, so 503 without a key.</summary>
    Custom,
}

/// <summary>
/// A validated <c>ResolveBodySchema</c>.
/// </summary>
public sealed record ResolveAnswer(ResolveAnswerKind Kind, int Index, string Text, string? Timezone);

/// <summary>
/// Port of <c>ResolveBodySchema</c> —
/// <c>z.object({answer: z.discriminatedUnion('type', [...]), timezone: …})</c>,
/// read with <c>safeParse</c> and reported through <c>error.flatten()</c>.
///
/// <para>
/// zod's <c>flatten()</c> keys every issue under <c>path[0]</c>, so an issue at
/// <c>answer.index</c> lands under <c>"answer"</c> — not <c>"answer.index"</c>.
/// That is why every issue below is filed at the top-level name.
/// </para>
/// </summary>
public static class ResolveAnswerBinder
{
    public const string Code = "invalid_answer";

    public const string Message = "Invalid answer payload.";

    /// <summary>Discriminator values, in schema order — zod prints them in that order.</summary>
    private static readonly string[] Discriminators = { "option", "custom" };

    public static ResolveAnswer Parse(ResolveBody body)
    {
        var issues = new List<ValidationIssue>();

        var timezone = ReadTimezone(body.Timezone, issues);
        var answer = ReadAnswer(body.Answer, issues);

        if (issues.Count > 0)
        {
            throw AppException.BadRequest(Code, Message, ValidationDetails.AsFlattened(issues));
        }

        return answer! with { Timezone = timezone };
    }

    private static ResolveAnswer? ReadAnswer(JsonElement answer, List<ValidationIssue> issues)
    {
        if (answer.ValueKind is JsonValueKind.Undefined)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.Required));
            return null;
        }

        if (answer.ValueKind != JsonValueKind.Object)
        {
            // A discriminated union fails its object check first.
            issues.Add(ValidationIssue.At(
                "answer",
                ZodMessages.ExpectedType("object", KindName(answer.ValueKind))));
            return null;
        }

        var discriminator = answer.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

        return discriminator switch
        {
            "option" => ReadOption(answer, issues),
            "custom" => ReadCustom(answer, issues),
            _ => InvalidDiscriminator(issues),
        };
    }

    private static ResolveAnswer? InvalidDiscriminator(List<ValidationIssue> issues)
    {
        issues.Add(ValidationIssue.At("answer", InvalidDiscriminatorMessage));
        return null;
    }

    /// <summary>
    /// zod's <c>invalid_union_discriminator</c> text. Single-quoted values joined
    /// with <c>" | "</c>, same as an enum failure.
    /// </summary>
    private static string InvalidDiscriminatorMessage =>
        $"Invalid discriminator value. Expected {string.Join(" | ", Discriminators.Select(v => $"'{v}'"))}";

    /// <summary><c>{type: 'option', index: z.number().int().min(0).max(3)}</c>.</summary>
    private static ResolveAnswer? ReadOption(JsonElement answer, List<ValidationIssue> issues)
    {
        if (!answer.TryGetProperty("index", out var index) || index.ValueKind == JsonValueKind.Undefined)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.Required));
            return null;
        }

        if (index.ValueKind != JsonValueKind.Number)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.ExpectedType("number", KindName(index.ValueKind))));
            return null;
        }

        if (!index.TryGetInt32(out var value))
        {
            // A non-integer number. zod's `.int()` reports its own message.
            issues.Add(ValidationIssue.At("answer", "Expected integer, received float"));
            return null;
        }

        if (value < 0)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.NumberTooSmall(0)));
            return null;
        }

        if (value > 3)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.NumberTooBig(3)));
            return null;
        }

        return new ResolveAnswer(ResolveAnswerKind.Option, value, string.Empty, null);
    }

    /// <summary><c>{type: 'custom', text: z.string().trim().min(1).max(500)}</c>.</summary>
    private static ResolveAnswer? ReadCustom(JsonElement answer, List<ValidationIssue> issues)
    {
        if (!answer.TryGetProperty("text", out var text) || text.ValueKind == JsonValueKind.Undefined)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.Required));
            return null;
        }

        if (text.ValueKind != JsonValueKind.String)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.ExpectedType("string", KindName(text.ValueKind))));
            return null;
        }

        // `.trim()` sits before the length checks, so a whitespace-only answer is
        // rejected rather than stored as "".
        var value = (text.GetString() ?? string.Empty).Trim();

        if (value.Length < 1)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.TooShort(1)));
            return null;
        }

        if (value.Length > 500)
        {
            issues.Add(ValidationIssue.At("answer", ZodMessages.TooLong(500)));
            return null;
        }

        return new ResolveAnswer(ResolveAnswerKind.Custom, 0, value, null);
    }

    /// <summary><c>z.string().min(1).max(64).optional()</c>.</summary>
    private static string? ReadTimezone(JsonElement timezone, List<ValidationIssue> issues)
    {
        if (timezone.ValueKind is JsonValueKind.Undefined)
        {
            return null;
        }

        if (timezone.ValueKind != JsonValueKind.String)
        {
            issues.Add(ValidationIssue.At(
                "timezone",
                ZodMessages.ExpectedType("string", KindName(timezone.ValueKind))));
            return null;
        }

        var value = timezone.GetString() ?? string.Empty;

        if (value.Length < 1)
        {
            issues.Add(ValidationIssue.At("timezone", ZodMessages.TooShort(1)));
            return null;
        }

        if (value.Length > 64)
        {
            issues.Add(ValidationIssue.At("timezone", ZodMessages.TooLong(64)));
            return null;
        }

        return value;
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };
}
