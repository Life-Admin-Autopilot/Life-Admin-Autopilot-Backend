using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// An auto-saved item on the wire. Embedded with <c>_id: false</c> in Mongoose,
/// so unlike a subtask it has no id of its own and no transform.
/// </summary>
public sealed class VoiceExtractedTaskDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "high";

    [JsonPropertyName("reviewReason")]
    public string ReviewReason { get; init; } = "clear";

    [JsonPropertyName("estimate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskEstimateDto? Estimate { get; init; }

    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DueAt { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    [JsonPropertyName("taskId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }
}

/// <summary>A held-for-review item on the wire.</summary>
public sealed class VoiceReviewItemDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "medium";

    [JsonPropertyName("reviewReason")]
    public string ReviewReason { get; init; } = "ambiguous_intent";

    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    [JsonPropertyName("estimate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskEstimateDto? Estimate { get; init; }

    [JsonPropertyName("dueRaw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DueRaw { get; init; }

    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DueAt { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}

/// <summary>
/// Wire shape of a voice note — the output of <c>VoiceNoteSchema.toJSON</c>.
///
/// <para>
/// <b>Eight fields are absent by design</b>, and their absence is the contract:
/// <c>storageKey</c>, <c>attempts</c>, <c>maxAttempts</c>, <c>lockedUntil</c>,
/// <c>nextRunAt</c>, <c>lastError</c>, <c>notifiedAt</c> — and
/// <c>clarifyItems</c>, which is the one that catches people. The clarify lane is
/// internal staging; it surfaces to clients as Clarifications on their own
/// endpoint, never on the voice-note payload. There is no property for any of
/// them here, so no future edit can leak one by accident.
/// </para>
///
/// <para>
/// <c>failureReason</c> deliberately SURVIVES the transform even though
/// <c>lastError</c> — which holds the same string — does not. One is a
/// plain-language sentence the error screen shows the user, the other is job
/// machinery.
/// </para>
///
/// <para>
/// <b>Property order follows Mongoose's EMISSION order, not the schema.</b>
/// KERNEL.md §6 says "schema order"; that is a good heuristic and it is wrong
/// here, exactly as <c>ScannedDocumentDto</c> already documents. Mongoose
/// serialises keys in the order the document first set them, so the create-time
/// fields come out in the order of the route's object literal
/// (<c>…status, clientCapturedAt, timezone, mimeType</c>) — note that puts them
/// BEFORE <c>extractedTasks</c>/<c>reviewItems</c>, which the schema declares
/// first but which arrive as defaults. Anything the worker or the review commit
/// fills in later — <c>transcript</c>, <c>failureReason</c>, <c>reviewedAt</c> —
/// lands AFTER <c>createdAt</c>/<c>updatedAt</c>. Confirmed against the two
/// captured examples in <c>paths.integrations.yaml</c>.
/// </para>
/// </summary>
public sealed class VoiceNoteDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; init; }

    [JsonPropertyName("byteSize")]
    public int ByteSize { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "app";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("clientCapturedAt")]
    public DateTime ClientCapturedAt { get; init; }

    [JsonPropertyName("timezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timezone { get; init; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("extractedTasks")]
    public IReadOnlyList<VoiceExtractedTaskDto> ExtractedTasks { get; init; } =
        Array.Empty<VoiceExtractedTaskDto>();

    [JsonPropertyName("reviewItems")]
    public IReadOnlyList<VoiceReviewItemDto> ReviewItems { get; init; } =
        Array.Empty<VoiceReviewItemDto>();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    // ---- Everything set AFTER creation -------------------------------------
    // Mongoose serialises a document's keys in the order they were first SET, and
    // MongoDB appends a newly-$set field to the end of the stored document, so a
    // field the worker or the review commit adds lands after createdAt/updatedAt.

    [JsonPropertyName("transcript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transcript { get; init; }

    [JsonPropertyName("failureReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; init; }

    [JsonPropertyName("reviewedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ReviewedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}
