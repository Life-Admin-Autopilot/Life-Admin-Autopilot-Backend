using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// <c>GET /me/clarifications</c> — <c>{clarifications, hasMore, nextCursor}</c>.
/// </summary>
public sealed class ClarificationListResponse
{
    [JsonPropertyName("clarifications")]
    public IReadOnlyList<ClarificationDto> Clarifications { get; init; } = Array.Empty<ClarificationDto>();

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    /// <summary>
    /// The last item's <c>createdAt</c>, or an explicit <c>null</c> on the final page.
    ///
    /// <para>
    /// Node builds this in plain JavaScript (<c>hasMore ? … : null</c>), so the key is
    /// ALWAYS present. The kernel pins <c>WhenWritingNull</c> globally — right for
    /// document DTOs, wrong here — so this opts back in.
    /// </para>
    /// </summary>
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? NextCursor { get; init; }
}

/// <summary>
/// <c>POST /me/clarifications</c> — <c>{clarification, clarifications, task, queueFull}</c>.
///
/// <para>
/// The task is ALWAYS present: a held item is a real matter with a question attached,
/// never a withheld one.
/// </para>
///
/// <para>
/// One matter can carry SEVERAL independently-answerable questions, so the row count
/// is <c>clarifications.Length</c> and <c>clarification</c> is the first of them —
/// kept so a caller written against the single-question response keeps working
/// unchanged.
/// </para>
/// </summary>
public sealed class ClarificationCreateResponse
{
    /// <summary>
    /// The FIRST row, or explicitly <c>null</c> — never omitted — when the
    /// open-question queue was already full. The caller has to be able to tell "no
    /// question was filed" from "the key is missing because the server is an older
    /// build".
    /// </summary>
    [JsonPropertyName("clarification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ClarificationDto? Clarification { get; init; }

    /// <summary>
    /// EVERY row created, in the order the questions were asked, all sharing the one
    /// <c>task.id</c>. Always present; empty exactly when <see cref="Clarification"/>
    /// is null.
    ///
    /// <para>
    /// Additive. A legacy single-question payload answers with one entry here and the
    /// same object under <c>clarification</c>, so nothing that reads only the old key
    /// changes shape.
    /// </para>
    /// </summary>
    [JsonPropertyName("clarifications")]
    public IReadOnlyList<ClarificationDto> Clarifications { get; init; } = Array.Empty<ClarificationDto>();

    [JsonPropertyName("task")]
    public TaskDto Task { get; init; } = new();

    /// <summary>
    /// True when the cap turned the hold into a plain create. Always present, so a
    /// caller can branch on it without treating absence as false.
    /// </summary>
    [JsonPropertyName("queueFull")]
    public bool QueueFull { get; init; }
}

/// <summary>
/// <c>POST /me/clarifications/{id}/defer</c> and <c>/drop</c> — <c>{clarification}</c>.
/// </summary>
public sealed class ClarificationEnvelope
{
    [JsonPropertyName("clarification")]
    public ClarificationDto Clarification { get; init; } = new();
}

/// <summary>
/// <c>GET /me/clarifications/by-ids</c> — what became of questions a caller
/// already holds the ids of.
///
/// <para>
/// The list endpoint cannot answer this. It returns VISIBLE OPEN rows, so a row
/// missing from it may have been resolved, dropped, or merely deferred out of
/// sight — three different things a chat transcript must render three different
/// ways. Absence is not an answer, so this reads the rows by id and reports
/// their status outright.
/// </para>
/// </summary>
public sealed class ClarificationStatusResponse
{
    [JsonPropertyName("clarifications")]
    public List<ClarificationStatusDto> Clarifications { get; init; } = new();
}

/// <summary>One row's outcome, plus the matter as it stands NOW.</summary>
public sealed class ClarificationStatusDto
{
    [JsonPropertyName("clarification")]
    public ClarificationDto Clarification { get; init; } = new();

    /// <summary>
    /// The task the answer patched, current — not the draft the question was
    /// raised against.
    ///
    /// <para>
    /// This is what lets a re-read transcript show the CONFIRMED time rather than
    /// the guess the card asked about. Null when the task has since been deleted.
    /// </para>
    /// </summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TaskDto? Task { get; init; }
}

/// <summary>
/// <c>POST /me/clarifications/{id}/resolve</c> — <c>{clarification, task}</c>.
/// </summary>
public sealed class ClarificationResolveResponse
{
    [JsonPropertyName("clarification")]
    public ClarificationDto Clarification { get; init; } = new();

    /// <summary>
    /// The updated task, RAW — <c>runTool</c>'s result carries no i18n overlay.
    ///
    /// <para>
    /// Explicitly <c>null</c> (never omitted) when the question was already closed,
    /// or when its task had been deleted and the question was therefore dropped.
    /// </para>
    /// </summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TaskDto? Task { get; init; }
}
