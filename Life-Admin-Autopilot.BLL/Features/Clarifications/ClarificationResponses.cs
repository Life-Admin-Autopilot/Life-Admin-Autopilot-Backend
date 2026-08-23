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

    /// <summary>
    /// What the matter this hold just filed collides with, if anything.
    ///
    /// <para>
    /// <b>The same three keys <c>createTask</c> answers with</b>, and that is the
    /// point: the chat card branches on <c>task.id &amp;&amp; conflicts.length</c>
    /// and renders the decision panel, so a hold reporting its clash the same way
    /// needs no client change to start being shown. Without them a held matter that
    /// landed on top of another said nothing in the conversation, and the clash was
    /// first seen days later on the matter's own sheet — which is where it had been
    /// all along, because <c>/me/tasks/{id}/conflicts</c> always found it.
    /// </para>
    /// </summary>
    [JsonPropertyName("conflicts")]
    public IReadOnlyList<ClarificationConflictDto> Conflicts { get; init; } =
        Array.Empty<ClarificationConflictDto>();

    /// <summary>Instants checked free against the same pool the clash was found in.</summary>
    [JsonPropertyName("suggestions")]
    public IReadOnlyList<DateTime> Suggestions { get; init; } = Array.Empty<DateTime>();

    [JsonPropertyName("suggestionReason")]
    public string SuggestionReason { get; init; } = string.Empty;
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

    /// <summary>
    /// What the answer just collided with, if anything.
    ///
    /// <para>
    /// <b>The gap this closes.</b> A held item is asked "what time on Tuesday?" and
    /// the user picks one — and until now nothing checked whether the time they
    /// picked was free. So the clash the question existed to prevent could be created
    /// by the answer to it, silently, with the question then marked resolved. Every
    /// other way of setting a date in this product is checked; this was the one route
    /// that wrote a due date without ever asking.
    /// </para>
    ///
    /// <para>
    /// <b>Reported, never enforced.</b> The item is already filed and the user has
    /// just answered a question about it; refusing the answer would strand it and
    /// re-ask something they have already decided. So the write stands, the question
    /// closes, and the clash comes back with times that are free — one more tap, of
    /// their choosing, rather than a wall.
    /// </para>
    ///
    /// <para>Empty on every ordinary resolve, which is almost all of them.</para>
    /// </summary>
    [JsonPropertyName("conflicts")]
    public IReadOnlyList<ClarificationConflictDto> Conflicts { get; init; } =
        Array.Empty<ClarificationConflictDto>();

    /// <summary>
    /// Free instants this matter could move to instead, soonest first. Only ever
    /// populated alongside a conflict, and only when one could be found.
    /// </summary>
    [JsonPropertyName("suggestions")]
    public IReadOnlyList<DateTime> Suggestions { get; init; } = Array.Empty<DateTime>();

    /// <summary>Which part of the day those slots came from — "evening", "working hours".</summary>
    [JsonPropertyName("suggestionReason")]
    public string SuggestionReason { get; init; } = string.Empty;
}

/// <summary>
/// One clash, in the same shape <c>/me/conflicts</c> and the propose route send, so
/// the client renders it with the component it already has.
/// </summary>
public sealed class ClarificationConflictDto
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("dueAt")]
    public DateTime? DueAt { get; init; }

    /// <summary><c>time_clash</c> or <c>duplicate</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
