using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// <c>askBodySchema</c> — a plain (NOT <c>.strict()</c>) zod object, so unknown
/// keys are stripped and the request succeeds. Verified live:
/// <c>{"question":"hi","bogus":1}</c> reaches the AI gate.
/// </summary>
public sealed class AskBody
{
    [JsonPropertyName("question")]
    public JsonElement Question { get; init; }

    [JsonPropertyName("scope")]
    public JsonElement Scope { get; init; }

    [JsonPropertyName("timezone")]
    public JsonElement Timezone { get; init; }

    /// <summary>
    /// Which entry path this turn came from — <c>chat</c>, <c>transcript</c>,
    /// <c>document</c> or <c>clarification</c>.
    ///
    /// <para>
    /// <b>Additive, and a deliberate divergence from Node.</b> The reference has no
    /// such field and strips it, so a client that sends one sees identical behaviour
    /// there. It exists because every path in the product converges on ONE planning
    /// agent, and the agent needs to know which one it is: the same sentence means
    /// different things typed, dictated, and extracted from a scan. Absent ⇒
    /// <c>chat</c>, which is what every existing caller already gets.
    /// </para>
    /// </summary>
    [JsonPropertyName("mode")]
    public JsonElement Mode { get; init; }

    /// <summary>
    /// Which conversation thread to append this turn to.
    ///
    /// <para>
    /// <b>Additive, like <c>mode</c>.</b> Absent ⇒ the user's most recent thread,
    /// which is what every client that predates multi-conversation support gets.
    /// The resolved id comes back as the stream's opening <c>conversation</c>
    /// frame, so a client that sent nothing still learns where its turn landed.
    /// </para>
    /// </summary>
    [JsonPropertyName("conversationId")]
    public JsonElement ConversationId { get; init; }
}

/// <summary><c>confirmToolBodySchema</c>. One required enum, nothing else.</summary>
public sealed class ConfirmToolBody
{
    [JsonPropertyName("action")]
    public JsonElement Action { get; init; }
}

/// <summary>
/// Body reading and the two zod chains this slice mirrors.
///
/// <para>
/// Two deliberate departures from calling <c>KernelBody.ReadAsync&lt;T&gt;</c>
/// straight, each covering a case where it diverges from express:
/// </para>
///
/// <list type="number">
///   <item>
///     <b>A top-level JSON primitive is a 500, not a 400.</b> <c>express.json()</c>
///     runs with <c>strict: true</c>, which accepts only an object or an array;
///     <c>"x"</c>, <c>42</c> and <c>null</c> are SyntaxErrors that fall through the
///     error handler as <c>500 internal_error</c>. Verified live. The kernel binder
///     answers 400 for those, so <see cref="ReadAsync{T}"/> rejects them first.
///     Slice D hit the same thing and worked around it locally — see
///     <c>NotificationEndpoints.ReadBodyRootAsync</c>.
///   </item>
///   <item>
///     <b>zod's enum has TWO message forms</b>, and only one of them says
///     "Invalid enum value." See <see cref="ReadEnum"/>.
///   </item>
/// </list>
/// </summary>
internal static class AiRequests
{
    public static readonly IReadOnlyList<string> Scopes = new[] { "personal" };

    public static readonly IReadOnlyList<string> ConfirmActions = new[] { "confirm", "decline" };

    /// <summary>
    /// The planning agent's four entry paths. This list IS the contract — the flow
    /// branches on exactly these strings, so an unrecognised one must be rejected
    /// at the edge rather than passed through to be silently ignored by the agent.
    /// </summary>
    public static readonly IReadOnlyList<string> AskModes =
        new[] { "chat", "transcript", "document", "clarification" };

    /// <summary>
    /// What an absent <c>mode</c> means. Declared here rather than borrowed from
    /// the Langflow binding on purpose: this route also serves
    /// <c>NotConfiguredAiProvider</c>, and a provider-agnostic route must not name a
    /// provider's type to validate its own request.
    /// </summary>
    public const string DefaultAskMode = "chat";

    /// <summary>
    /// Reads the body with express's semantics, then hands the bytes to the kernel
    /// binder so the deserialization and unknown-key handling stay in one place.
    /// </summary>
    public static async Task<T> ReadAsync<T>(
        HttpContext context,
        KernelBodyOptions options,
        CancellationToken cancellationToken = default)
        where T : new()
    {
        // express.json() parses ONLY application/json; every other content type
        // leaves req.body = {} without the stream ever being read, so neither the
        // 256kb ceiling nor a syntax error can fire.
        if (!KernelBody.IsJsonContentType(context.Request))
        {
            return new T();
        }

        var bytes = await KernelBody
            .ReadBytesAsync(context.Request, KernelJson.MaxJsonBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            return new T();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new BodyReadException("malformed JSON body", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                throw new BodyReadException("body-parser strict mode: root must be an object or array");
            }
        }

        // Rewind for the kernel binder. An array still reaches it, and it produces
        // the correct `formErrors: ["Expected object, received array"]`.
        context.Request.Body = new MemoryStream(bytes, writable: false);

        return await KernelBody.ReadAsync<T>(context, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>z.string().trim().min(1, m1).max(2000, m2)</c> with CUSTOM messages. The
    /// <c>.trim()</c> is first, so a whitespace-only question is "cannot be empty"
    /// rather than being stored as <c>""</c>.
    /// </summary>
    public static string? ReadQuestion(BodyIssues issues, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            issues.Add("question", ZodMessages.Required);
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add("question", ZodMessages.ExpectedType("string", TypeName(element)));
            return null;
        }

        var value = (element.GetString() ?? string.Empty).Trim();

        if (value.Length < 1)
        {
            issues.Add("question", "question cannot be empty");
            return null;
        }

        if (value.Length > 2000)
        {
            issues.Add("question", "question cannot exceed 2000 characters");
            return null;
        }

        return value;
    }

    /// <summary><c>z.string().min(1).max(64).optional()</c> — plain zod messages.</summary>
    public static string? ReadTimezone(BodyIssues issues, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add("timezone", ZodMessages.ExpectedType("string", TypeName(element)));
            return null;
        }

        var value = element.GetString() ?? string.Empty;

        if (value.Length < 1)
        {
            issues.Add("timezone", ZodMessages.TooShort(1));
            return null;
        }

        if (value.Length > 64)
        {
            issues.Add("timezone", ZodMessages.TooLong(64));
            return null;
        }

        return value;
    }

    /// <summary>
    /// <c>z.enum([...])</c>, with <b>both</b> of zod's message forms — the
    /// distinction is observable and a single form gets one of them wrong:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     a STRING that is not a member → <c>invalid_enum_value</c>:
    ///     <c>"Invalid enum value. Expected 'confirm' | 'decline', received 'nope'"</c>
    ///   </item>
    ///   <item>
    ///     anything that is not a string → <c>invalid_type</c>, with NO
    ///     "Invalid enum value." prefix and the JS type rather than the value:
    ///     <c>"Expected 'confirm' | 'decline', received null"</c>
    ///   </item>
    /// </list>
    ///
    /// Both verified live on <c>/ai/ask</c> and <c>/ai/tools/confirm</c>.
    /// </summary>
    public static string? ReadEnum(
        BodyIssues issues,
        JsonElement element,
        string field,
        IReadOnlyList<string> allowed,
        string? fallback,
        bool required)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            if (required)
            {
                issues.Add(field, ZodMessages.Required);
            }

            // `.default('personal')` applies to `undefined` only — never to null.
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(field, $"Expected {Quoted(allowed)}, received {TypeName(element)}");
            return null;
        }

        var value = element.GetString();
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
        {
            issues.Add(field, $"Invalid enum value. Expected {Quoted(allowed)}, received '{value}'");
            return null;
        }

        return value;
    }

    private static string Quoted(IReadOnlyList<string> allowed) =>
        string.Join(" | ", allowed.Select(v => $"'{v}'"));

    /// <summary>The JS <c>typeof</c>-ish name zod reports, with null called out.</summary>
    private static string TypeName(JsonElement element) => element.ValueKind switch
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

/// <summary>
/// Accumulates zod issues so every failure ships in one response, then renders
/// them through the <c>{formErrors, fieldErrors}</c> mapper — the shape both AI
/// bodies use.
/// </summary>
internal sealed class BodyIssues
{
    private readonly List<ValidationIssue> _issues = new();

    public void Add(string field, string message) => _issues.Add(ValidationIssue.At(field, message));

    public void ThrowIfInvalid(string code, string message)
    {
        if (_issues.Count == 0)
        {
            return;
        }

        throw AppException.BadRequest(code, message, ValidationDetails.AsFlattened(_issues));
    }
}
