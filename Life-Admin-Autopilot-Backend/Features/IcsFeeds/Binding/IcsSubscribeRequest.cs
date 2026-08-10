using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.IcsFeeds.Binding;

/// <summary>
/// <c>POST /me/ics-feeds</c> body. The Node schema is a PLAIN <c>z.object</c>, not
/// <c>.strict()</c>, so unknown keys are stripped rather than rejected — read it
/// with <c>KernelBodyOptions.Lenient</c>.
/// </summary>
/// <remarks>
/// <c>JsonElement</c>, not <c>JsonElement?</c>. A nullable one collapses an absent
/// key and an explicit <c>null</c> into the same value, and zod tells them apart:
/// <c>{"url":null}</c> is <c>received: "null"</c> / "Expected string, received null"
/// while an absent key is <c>received: "undefined"</c> / "Required". Both verified
/// live. Default(JsonElement) has <see cref="JsonValueKind.Undefined"/>, which is
/// exactly the distinction needed.
/// </remarks>
public sealed class IcsSubscribeBody
{
    [JsonPropertyName("url")]
    public JsonElement Url { get; init; }

    [JsonPropertyName("label")]
    public JsonElement Label { get; init; }

    [JsonPropertyName("domain")]
    public JsonElement Domain { get; init; }
}

/// <param name="Url">Raw, untrimmed — the schema does not trim it; the URL guard does.</param>
/// <param name="Label">Already trimmed, because <c>.trim()</c> precedes <c>.min(1)</c> in the chain.</param>
public readonly record struct IcsSubscribeInput(string Url, string Label, string Domain);

/// <summary>
/// Validates the subscribe body and emits <c>invalid_feed</c> the way — and ONLY the
/// way — this one Node route does.
///
/// <para>
/// <b>This is the single route in the whole API whose <c>details</c> is the RAW zod
/// issues ARRAY.</b> Every other route emits either the dot-joined
/// <c>{path,message}</c> array or the <c>{formErrors,fieldErrors}</c> object from
/// <c>.flatten()</c>. The raw array carries per-code members the other two shapes
/// throw away — <c>minimum</c>/<c>maximum</c>/<c>type</c>/<c>inclusive</c>/
/// <c>exact</c> on a length failure, <c>options</c> on a bad enum value — and
/// <c>path</c> is an ARRAY, not a dotted string. Reproduced member-for-member
/// against the live reference; see the tests.
/// </para>
///
/// <para>
/// Issues come out in SCHEMA order (url, label, domain), which is what zod does and
/// what the array's ordering makes observable.
/// </para>
/// </summary>
public static class IcsSubscribeValidator
{
    public const string Code = "invalid_feed";
    public const string Message = "Check the address, name and category.";

    /// <summary>zod renders an enum's expected type as the quoted union.</summary>
    private static readonly string DomainUnion =
        string.Join(" | ", TaskVocabulary.Domains.Select(d => $"'{d}'"));

    public static IcsSubscribeInput Validate(IcsSubscribeBody body)
    {
        var issues = new List<object>();

        var url = ReadString(body.Url, "url", min: 1, max: IcsFeedVocabulary.MaxUrlLength, trim: false, issues);
        var label = ReadString(body.Label, "label", min: 1, max: IcsFeedVocabulary.MaxLabelLength, trim: true, issues);
        var domain = ReadDomain(body.Domain, issues);

        if (issues.Count > 0)
        {
            throw AppException.BadRequest(Code, Message, issues);
        }

        return new IcsSubscribeInput(url!, label!, domain!);
    }

    /// <param name="trim">
    /// <c>label</c> is <c>.trim().min(1).max(80)</c>: the trim is a transform EARLIER
    /// in the chain, so a whitespace-only label fails <c>min(1)</c> rather than being
    /// stored as an empty string. <c>url</c> has no trim. Verified live.
    /// </param>
    private static string? ReadString(
        JsonElement value,
        string path,
        int min,
        int max,
        bool trim,
        List<object> issues)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            var received = ZodTypeName(value.ValueKind);
            var message = received == "undefined" ? "Required" : $"Expected string, received {received}";
            issues.Add(InvalidType("string", received, path, message));
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        if (trim)
        {
            text = text.Trim();
        }

        var failed = false;

        if (text.Length < min)
        {
            issues.Add(new TooSmallIssue
            {
                Minimum = min,
                Message = $"String must contain at least {min} character(s)",
                Path = new[] { path },
            });
            failed = true;
        }

        if (text.Length > max)
        {
            issues.Add(new TooBigIssue
            {
                Maximum = max,
                Message = $"String must contain at most {max} character(s)",
                Path = new[] { path },
            });
            failed = true;
        }

        return failed ? null : text;
    }

    private static string? ReadDomain(JsonElement value, List<object> issues)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            var received = ZodTypeName(value.ValueKind);
            var message = received == "undefined" ? "Required" : $"Expected {DomainUnion}, received {received}";
            issues.Add(InvalidType(DomainUnion, received, "domain", message));
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        if (TaskVocabulary.Domains.Contains(text))
        {
            return text;
        }

        issues.Add(new InvalidEnumValueIssue
        {
            Received = text,
            Options = TaskVocabulary.Domains,
            Path = new[] { "domain" },
            Message = $"Invalid enum value. Expected {DomainUnion}, received '{text}'",
        });

        return null;
    }

    private static InvalidTypeIssue InvalidType(string expected, string received, string path, string message) =>
        new()
        {
            Expected = expected,
            Received = received,
            Path = new[] { path },
            Message = message,
        };

    private static string ZodTypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    // ---- The raw zod issue shapes, member for member -----------------------
    //
    // These are NOT the kernel's ValidationIssue: that type models the members the
    // other two `details` shapes need, and projecting through it would silently drop
    // `minimum` / `options` / the rest.

    private sealed class InvalidTypeIssue
    {
        [JsonPropertyName("code")]
        public string Code => "invalid_type";

        [JsonPropertyName("expected")]
        public string Expected { get; init; } = string.Empty;

        [JsonPropertyName("received")]
        public string Received { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }

    private sealed class TooSmallIssue
    {
        [JsonPropertyName("code")]
        public string Code => "too_small";

        [JsonPropertyName("minimum")]
        public int Minimum { get; init; }

        [JsonPropertyName("type")]
        public string Type => "string";

        [JsonPropertyName("inclusive")]
        public bool Inclusive => true;

        [JsonPropertyName("exact")]
        public bool Exact => false;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
    }

    private sealed class TooBigIssue
    {
        [JsonPropertyName("code")]
        public string Code => "too_big";

        [JsonPropertyName("maximum")]
        public int Maximum { get; init; }

        [JsonPropertyName("type")]
        public string Type => "string";

        [JsonPropertyName("inclusive")]
        public bool Inclusive => true;

        [JsonPropertyName("exact")]
        public bool Exact => false;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
    }

    private sealed class InvalidEnumValueIssue
    {
        [JsonPropertyName("received")]
        public string Received { get; init; } = string.Empty;

        [JsonPropertyName("code")]
        public string Code => "invalid_enum_value";

        [JsonPropertyName("options")]
        public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

        [JsonPropertyName("path")]
        public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;
    }
}
