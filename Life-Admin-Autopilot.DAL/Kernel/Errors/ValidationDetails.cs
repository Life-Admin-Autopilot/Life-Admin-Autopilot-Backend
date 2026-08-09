using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Kernel.Errors;

/// <summary>
/// One validation issue, in the raw zod shape. Only the <c>invalid_feed</c>
/// mapper emits this verbatim; the other two mappers project from it.
/// </summary>
public sealed class ValidationIssue
{
    /// <summary>zod issue code, e.g. <c>invalid_type</c>, <c>too_small</c>, <c>custom</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "custom";

    [JsonPropertyName("expected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expected { get; init; }

    [JsonPropertyName("received")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Received { get; init; }

    /// <summary>Path segments, outermost first. Empty for a whole-object issue.</summary>
    [JsonPropertyName("path")]
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    public static ValidationIssue At(string path, string message) =>
        new() { Path = path.Length == 0 ? Array.Empty<string>() : path.Split('.'), Message = message };

    /// <summary>A whole-object issue with no field path (zod's <c>formErrors</c> lane).</summary>
    public static ValidationIssue Form(string message) => new() { Message = message };
}

/// <summary>zod's <c>error.flatten()</c> output.</summary>
public sealed class FlattenedValidationDetails
{
    [JsonPropertyName("formErrors")]
    public IReadOnlyList<string> FormErrors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Keyed by the TOP-LEVEL field name only. An issue at <c>mic.quality</c> is
    /// listed under <c>"mic"</c> — zod's flatten() uses <c>issue.path[0]</c>.
    /// Verified live against the Node server.
    /// </summary>
    [JsonPropertyName("fieldErrors")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>
/// One field/message pair, dot-joined path. The <c>validation_error</c> lane.
/// </summary>
public sealed class PathMessageDetail
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// The THREE — and only three — <c>details</c> shapes the Node server emits.
/// They are deliberately separate, explicitly-selected mappers rather than one
/// generic one, because picking the wrong shape is a silent parity break that
/// no status-code check catches.
///
/// <list type="table">
///   <item>
///     <term><see cref="AsPathMessageArray"/></term>
///     <description>
///       Routes mirroring a THROWING zod <c>.parse()</c>. Rendered by the error
///       middleware as code <c>validation_error</c>, message
///       <c>"Request validation failed"</c>, status 400, details = ARRAY of
///       <c>{path,message}</c> with a dot-joined path.
///       Node call sites: auth.password, auth.email, auth.session, auth.magic,
///       me.notifications.
///     </description>
///   </item>
///   <item>
///     <term><see cref="AsFlattened"/></term>
///     <description>
///       Routes mirroring <c>.safeParse()</c> + <c>error.flatten()</c>. The
///       route picks its own code (<c>invalid_body</c>, <c>invalid_query</c>,
///       <c>invalid_code</c>, <c>invalid_metadata</c>, <c>invalid_review</c>,
///       <c>invalid_answer</c>) and its own message; details =
///       <c>{formErrors, fieldErrors}</c>. This is the majority shape.
///     </description>
///   </item>
///   <item>
///     <term><see cref="AsRawIssues"/></term>
///     <description>
///       <c>invalid_feed</c> only (me.icsFeeds). details = the raw issues ARRAY,
///       each with <c>code/expected/received/path(ARRAY)/message</c>.
///     </description>
///   </item>
/// </list>
/// </summary>
public static class ValidationDetails
{
    /// <summary>Shape 1 — <c>validation_error</c>. Dot-joined path, message only.</summary>
    public static IReadOnlyList<PathMessageDetail> AsPathMessageArray(IEnumerable<ValidationIssue> issues) =>
        issues
            .Select(i => new PathMessageDetail { Path = string.Join('.', i.Path), Message = i.Message })
            .ToList();

    /// <summary>
    /// Shape 2 — zod <c>flatten()</c>. Issues with an empty path land in
    /// <c>formErrors</c>; everything else is grouped under <c>path[0]</c>,
    /// preserving issue order both between and within keys.
    /// </summary>
    public static FlattenedValidationDetails AsFlattened(IEnumerable<ValidationIssue> issues)
    {
        var formErrors = new List<string>();
        var fieldErrors = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var issue in issues)
        {
            if (issue.Path.Count == 0)
            {
                formErrors.Add(issue.Message);
                continue;
            }

            var key = issue.Path[0];
            if (!fieldErrors.TryGetValue(key, out var existing))
            {
                fieldErrors[key] = new List<string> { issue.Message };
                continue;
            }

            ((List<string>)existing).Add(issue.Message);
        }

        return new FlattenedValidationDetails { FormErrors = formErrors, FieldErrors = fieldErrors };
    }

    /// <summary>Shape 3 — <c>invalid_feed</c>. The issues array, untouched.</summary>
    public static IReadOnlyList<ValidationIssue> AsRawIssues(IEnumerable<ValidationIssue> issues) =>
        issues.ToList();

    /// <summary>
    /// zod's message for a <c>.strict()</c> object that received extra keys.
    /// Emitted as a form-level (path-less) error, so it lands in
    /// <c>formErrors</c>. Verified live:
    /// <c>"Unrecognized key(s) in object: 'bogus'"</c>.
    /// </summary>
    public static ValidationIssue UnrecognizedKeys(IEnumerable<string> keys) =>
        new()
        {
            Code = "unrecognized_keys",
            Message = $"Unrecognized key(s) in object: {string.Join(", ", keys.Select(k => $"'{k}'"))}",
        };
}
