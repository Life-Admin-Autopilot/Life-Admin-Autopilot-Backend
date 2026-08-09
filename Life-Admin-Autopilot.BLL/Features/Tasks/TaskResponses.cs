using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// The Matters slice's response envelopes — only those that need to be named.
///
/// <para>
/// <b>Why these are named types and not anonymous objects.</b> The kernel pins
/// <c>DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull</c> globally,
/// which is right for Mongoose-shaped documents: an unset optional is genuinely
/// absent from the JSON, never <c>null</c>. But a handful of ENVELOPE fields are
/// the opposite — Node builds them in JavaScript, not Mongoose, and ships an
/// explicit <c>null</c> that the client branches on. An anonymous object inherits
/// the global rule and silently drops the key, so each of those carries
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c> to opt back in.
/// </para>
///
/// <para>
/// Verified live against <c>:4200</c>: <c>GET /me/tasks</c> on a complete page
/// answers <c>"nextCursor": null</c>, and <c>GET /me/tasks/categorize/pending</c>
/// with nothing staged answers <c>"proposal": null</c> — not <c>{}</c>.
/// </para>
///
/// <para>
/// Envelopes with no nullable member stay anonymous at the call site: a single
/// key cannot have a key-order or null-omission bug, so a named type would be
/// ceremony without a guarantee.
/// </para>
/// </summary>
public sealed class TaskListResponse
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskDto> Tasks { get; init; } = Array.Empty<TaskDto>();

    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>Explicit <c>null</c> on the last page — the client stops paging on it.</summary>
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NextCursor { get; init; }
}

/// <summary>
/// <c>DELETE /me/tasks/{id}</c>. The task itself is NOT echoed — the client
/// already has it and only needs the handle to undo with.
/// </summary>
public sealed class TaskDeleteResponse
{
    [JsonPropertyName("undoToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UndoToken { get; init; }
}

public sealed class BulkWarningsDto
{
    [JsonPropertyName("fromDocuments")]
    public int FromDocuments { get; init; }

    [JsonPropertyName("remindersFired")]
    public int RemindersFired { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

public sealed class BulkPreviewResponse
{
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("warnings")]
    public BulkWarningsDto Warnings { get; init; } = new();

    [JsonPropertyName("sample")]
    public IReadOnlyList<TaskDto> Sample { get; init; } = Array.Empty<TaskDto>();
}

public sealed class BulkApplyResponse
{
    [JsonPropertyName("affected")]
    public int Affected { get; init; }

    /// <summary>
    /// Explicit <c>null</c> when nothing changed: a no-op is never journaled, so
    /// there is no op to undo.
    /// </summary>
    [JsonPropertyName("undoToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UndoToken { get; init; }

    [JsonPropertyName("warnings")]
    public BulkWarningsDto Warnings { get; init; } = new();
}

/// <summary>
/// <c>GET /me/tasks/categorize/pending</c>. <c>proposal</c> is an explicit
/// <c>null</c> when nothing is staged, never an omitted key.
/// </summary>
public sealed class CategorizePendingResponse
{
    [JsonPropertyName("proposal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public PendingProposalDto? Proposal { get; init; }
}
