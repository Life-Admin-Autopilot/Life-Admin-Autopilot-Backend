using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>One pre-resolved suggested answer, dates already normalised.</summary>
public readonly record struct HoldOption(string Label, DateTime? DueAt, string? Title, string? Notes);

/// <summary>
/// A validated <c>holdForClarificationArgs</c>. Dates arrive as the RAW strings the
/// caller sent — normalisation needs the timezone and happens inside the service, so
/// the guess and every option go through one code path rather than two.
/// </summary>
public sealed record HoldInput(
    string Title,
    string Domain,
    string Question,
    string Kind,
    string? Priority,
    IReadOnlyList<string>? Tags,
    string? Notes,
    string? DueAtGuess,
    string? CostOfWrong,
    IReadOnlyList<HoldRawOption> Options,
    string? SourceText,
    string? Timezone);

/// <summary>An option as it came off the wire — <c>dueAt</c> not yet an instant.</summary>
public readonly record struct HoldRawOption(string Label, string? DueAt, string? Title, string? Notes);

/// <summary>
/// What the hold produced. <see cref="Clarification"/> is null ONLY when the queue
/// was full, in which case the task alone is the answer.
/// </summary>
public sealed record HoldOutcome(TaskDocument Task, ClarificationDocument? Clarification, bool QueueFull);

/// <summary>
/// Port of <c>runHoldForClarification</c> in
/// <c>server/src/modules/ai/toolRunner.ts</c> — the whole of it, task included.
///
/// <para>
/// <b>Why the route creates the task too.</b> In Node the planning agent runs
/// IN-PROCESS, so this function is a private call and needs no endpoint. Our agent
/// runs in Langflow, outside the API, and the only thing it can do is make HTTP
/// requests — so the semantics have to live behind a route or they do not exist at
/// all. Splitting them (agent creates the task, then posts the question) would move
/// the one rule that matters — a guessed date must not be able to fire — into
/// model-adjacent Python that nothing enforces. It is here instead.
/// </para>
///
/// <para>
/// <b>The task is created ALWAYS, question or no question.</b> Withholding it left a
/// captured item invisible — not in Matters, not searchable, not deletable — until
/// the user answered. What is withheld now is the REMINDER, and only when a wrong
/// date is expensive: a <c>high</c>-cost item lands as <c>kind:'list'</c> and cannot
/// fire, while a <c>low</c>-cost one may nudge on the guess because being wrong there
/// just means rescheduling.
/// </para>
/// </summary>
public sealed class ClarificationHoldService
{
    /// <summary>
    /// <c>MAX_OPEN_CLARIFICATIONS</c>. Past the cap the item is filed with the guess
    /// and the question is dropped: a slightly-wrong but VISIBLE task beats a
    /// question the user never reaches.
    /// </summary>
    public const int MaxOpenClarifications = 12;

    private readonly TaskWriteService _tasks;
    private readonly ClarificationRepository _clarifications;
    private readonly ReminderPlanner _reminders;

    public ClarificationHoldService(
        TaskWriteService tasks,
        ClarificationRepository clarifications,
        ReminderPlanner reminders)
    {
        _tasks = tasks;
        _clarifications = clarifications;
        _reminders = reminders;
    }

    public async Task<HoldOutcome> HoldAsync(
        ObjectId userId,
        HoldInput input,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        var options = input.Options
            .Select(o => new HoldOption(
                o.Label,
                o.DueAt is null ? null : HoldTimeNormalizer.Normalize(o.DueAt, input.Timezone),
                o.Title,
                o.Notes))
            .ToList();

        // The best guess we have, in priority order: an explicit one, else the
        // model's most-likely option — it orders them.
        var rawGuess = input.DueAtGuess;
        var dueAt = rawGuess is not null
            ? HoldTimeNormalizer.Normalize(rawGuess, input.Timezone)
            : options.FirstOrDefault().DueAt;

        // Defaults to 'high': if the model felt the item was worth asking about, do
        // not act on the guess.
        var costOfWrong = input.CostOfWrong ?? ClarificationVocabulary.CostHigh;
        var priority = input.Priority ?? "normal";
        var tags = input.Tags ?? Array.Empty<string>();

        var kind = dueAt.HasValue && costOfWrong == ClarificationVocabulary.CostLow ? "reminder" : "list";

        var task = await _tasks
            .CreateAsync(
                userId,
                new TaskCreateInput(
                    input.Title,
                    input.Domain,
                    kind,
                    priority,
                    tags,
                    dueAt,
                    input.Notes,
                    Estimate: null,
                    SourceVoiceNoteId: null),
                now,
                cancellationToken)
            .ConfigureAwait(false);

        // `runCreate` schedules the rules floor for a dated reminder. Reachable here
        // only on the 'low'-cost branch — which is exactly the branch that is allowed
        // to fire on a guess. Note POST /me/tasks does NOT do this, in either server,
        // so an agent that created the task itself would leave the schedule empty.
        if (task.Kind == "reminder" && task.DueAt.HasValue)
        {
            await _reminders.SetRulesRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
        }

        // Queue full → the task above is the whole answer. Skip the question rather
        // than growing a pile the user never reaches.
        if (await IsAtCapAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return new HoldOutcome(task, null, QueueFull: true);
        }

        var clarification = new ClarificationDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            TaskId = task.Id,
            Status = ClarificationVocabulary.Open,
            Draft = new ClarificationDraftDocument
            {
                Title = input.Title,
                Domain = input.Domain,
                Priority = priority,
                Notes = input.Notes,
                Tags = tags.ToList(),
                DueAt = dueAt,
            },
            Question = input.Question,
            Kind = input.Kind,
            CostOfWrong = costOfWrong,
            Options = options
                .Select(o => new ClarificationOptionDocument
                {
                    Label = o.Label,
                    DueAt = o.DueAt,
                    Title = o.Title,
                    Notes = o.Notes,
                })
                .ToList(),
            SourceText = SourceQuote.Clamp(input.SourceText),

            // No sourceKey. That is the VOICE lane's note-scoped idempotency key, and
            // the partial unique index is keyed on its presence; a chat-born hold is a
            // fresh create every time, which is what the open cap above exists to bound.
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _clarifications.InsertAsync(clarification, cancellationToken).ConfigureAwait(false);

        return new HoldOutcome(task, clarification, QueueFull: false);
    }

    /// <summary>
    /// <c>isAtOpenClarificationCap</c> — a bare <c>{userId, status:'open'}</c> count.
    ///
    /// <para>
    /// <b>Deliberately not <c>VisibleOpen()</c>.</b> Every surface that LISTS or COUNTS
    /// held items for display must compose that predicate, and this is neither: a
    /// question the user skipped is still queued and still comes back, so it still
    /// occupies a slot. Counting only the currently-visible ones would let a user with
    /// twelve deferred questions accumulate twelve more.
    /// </para>
    /// </summary>
    private async Task<bool> IsAtCapAsync(ObjectId userId, CancellationToken cancellationToken) =>
        await _clarifications.CountOpenAsync(userId, cancellationToken).ConfigureAwait(false)
        >= MaxOpenClarifications;
}
