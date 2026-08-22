using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Kernel.Dtos;

public sealed class ClarificationOptionDto
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DueAt { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    /// <summary>
    /// i18n key for <see cref="Label"/> when the SERVER composed this chip. Absent
    /// on the chat lane, whose chips the model already wrote in the user's own
    /// language, and on every row predating it — so a client falls back to
    /// <see cref="Label"/> rather than assuming this is here.
    /// </summary>
    [JsonPropertyName("labelKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LabelKey { get; init; }

    [JsonPropertyName("labelParams")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? LabelParams { get; init; }
}

public sealed class ClarificationDraftDto
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DueAt { get; init; }
}

/// <summary>
/// Wire shape of a Clarification. The transform drops <c>_id</c>, <c>__v</c> AND
/// <c>sourceKey</c> — the last is an internal idempotency key that must never
/// reach a client.
/// </summary>
public sealed class ClarificationDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    // Omitted, never null and never a zero id: legacy rows predate the required
    // constraint and the reference leaves the key out entirely for them.
    [JsonPropertyName("taskId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "open";

    [JsonPropertyName("draft")]
    public ClarificationDraftDto Draft { get; init; } = new();

    [JsonPropertyName("question")]
    public string Question { get; init; } = string.Empty;

    /// <summary>
    /// i18n key for <see cref="Question"/>, present only on questions the SERVER
    /// composed — the voice lane, where the AI never asks and the sentence was
    /// therefore written in English whatever the user spoke. A client that has the
    /// catalogue renders this instead, so the card matches the chrome around it and
    /// re-renders correctly if the app language changes later.
    /// </summary>
    [JsonPropertyName("questionKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuestionKey { get; init; }

    [JsonPropertyName("questionParams")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? QuestionParams { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "date";

    [JsonPropertyName("costOfWrong")]
    public string CostOfWrong { get; init; } = "high";

    [JsonPropertyName("options")]
    public IReadOnlyList<ClarificationOptionDto> Options { get; init; } = Array.Empty<ClarificationOptionDto>();

    [JsonPropertyName("sourceText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceText { get; init; }

    [JsonPropertyName("deferredUntil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DeferredUntil { get; init; }

    [JsonPropertyName("answer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Answer { get; init; }

    [JsonPropertyName("resolvedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ResolvedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}
