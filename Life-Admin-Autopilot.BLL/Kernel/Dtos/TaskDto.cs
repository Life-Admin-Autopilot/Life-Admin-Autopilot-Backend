using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Kernel.Dtos;

/// <summary>
/// Wire shape of a subtask. Mongoose does NOT apply a parent schema's transform
/// to embedded subdocuments, so <c>SubtaskSchema</c> carries its own
/// <c>toJSON</c> — without it subtasks serialize with <c>_id</c> and no <c>id</c>,
/// which breaks React list keys and subtask mutate-by-id on the client.
/// </summary>
public sealed class SubtaskDto
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("done")]
    public bool Done { get; init; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}

public sealed class TaskEstimateDto
{
    [JsonPropertyName("minMinutes")]
    public int MinMinutes { get; init; }

    [JsonPropertyName("maxMinutes")]
    public int MaxMinutes { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

public sealed class ReminderEntryDto
{
    [JsonPropertyName("at")]
    public DateTime At { get; init; }

    [JsonPropertyName("firedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FiredAt { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "due";
}

public sealed class TaskTranslationDto
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    [JsonPropertyName("subtasks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Subtasks { get; init; }

    [JsonPropertyName("at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? At { get; init; }
}

/// <summary>
/// Wire shape of a Task — the output of <c>TaskSchema.toJSON</c>.
///
/// <para>
/// Property order matches the Mongoose schema declaration order, with
/// <c>id</c> and the derived <c>priorityRank</c> appended, because that is the
/// order Node emits. <c>deletedAt</c> is present: Node's transform does not strip
/// it, and the Trash view reads it.
/// </para>
///
/// <para>Nullable members are omitted rather than emitted as <c>null</c> — Mongoose
/// simply does not store unset optional fields, so they never appear.</para>
/// </summary>
public sealed class TaskDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "list";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "open";

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("subtasks")]
    public IReadOnlyList<SubtaskDto> Subtasks { get; init; } = Array.Empty<SubtaskDto>();

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DueAt { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    [JsonPropertyName("sourceLocale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceLocale { get; init; }

    [JsonPropertyName("i18n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, TaskTranslationDto>? I18n { get; init; }

    [JsonPropertyName("sourceVoiceNoteId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceVoiceNoteId { get; init; }

    [JsonPropertyName("sourceDocumentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceDocumentId { get; init; }

    [JsonPropertyName("sourceTaskKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceTaskKey { get; init; }

    [JsonPropertyName("externalSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalSource { get; init; }

    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("timePrecision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimePrecision { get; init; }

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Confidence { get; init; }

    [JsonPropertyName("estimate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskEstimateDto? Estimate { get; init; }

    /// <summary>
    /// What this matter costs. Absent on most matters — an amount is the
    /// exception, not the rule — so every surface renders without it.
    /// </summary>
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoneyDto? Amount { get; init; }

    [JsonPropertyName("completedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CompletedAt { get; init; }

    [JsonPropertyName("snoozedUntil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? SnoozedUntil { get; init; }

    [JsonPropertyName("reminders")]
    public IReadOnlyList<ReminderEntryDto> Reminders { get; init; } = Array.Empty<ReminderEntryDto>();

    [JsonPropertyName("deletedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DeletedAt { get; init; }

    [JsonPropertyName("rescheduleCount")]
    public int RescheduleCount { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Derived, never stored. Lets the client sort and compare priorities without
    /// duplicating <c>PRIORITY_RANK</c>.
    /// </summary>
    [JsonPropertyName("priorityRank")]
    public int PriorityRank { get; init; }
}
