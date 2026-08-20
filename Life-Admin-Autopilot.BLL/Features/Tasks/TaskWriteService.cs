using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// A Mongoose document-level validation failure.
///
/// <para>
/// <b>Deliberately NOT an <see cref="AppException"/>.</b> Node's error handler
/// recognises its own <c>AppError</c> and zod's <c>ZodError</c>; a Mongoose
/// <c>ValidationError</c> matches neither, so it falls through to the generic
/// handler and the client sees <b>500 <c>internal_error</c></b>. Both reachable
/// 500s in this slice arrive here, and both are pinned by the contract — see
/// <see cref="TaskWriteService"/>.
/// </para>
/// </summary>
public sealed class TaskDocumentInvalidException : Exception
{
    public TaskDocumentInvalidException(string message)
        : base(message)
    {
    }
}

/// <summary>Validated create payload, already normalised by the route.</summary>
public sealed record TaskCreateInput(
    string Title,
    string Domain,
    string? Kind,
    string? Priority,
    IReadOnlyList<string>? Tags,
    DateTime? DueAt,
    string? Notes,
    TaskEstimateDocument? Estimate,
    MoneyDocument? Amount,
    ObjectId? SourceVoiceNoteId);

/// <summary>
/// Single-task writes: create, patch, and the three subtask mutations.
///
/// <para>Multi-task work is NOT here — it goes through the kernel's
/// <c>BulkService</c>, the only journaled write path.</para>
/// </summary>
public sealed class TaskWriteService
{
    private readonly TaskRepository _tasks;
    private readonly Knowledge.KnowledgeService? _knowledge;
    private readonly ReminderPlanner? _reminders;

    /// <summary>
    /// <paramref name="knowledge"/> is optional so this slice still stands up when
    /// the Knowledge slice is not registered — and so the existing tests that
    /// construct this service by hand keep compiling. When it IS present, every
    /// create and patch re-indexes the task for RAG ("every task is embedded, not
    /// just documents" — the ai_flow diagram). Ingest swallows its own failures, so
    /// this can never turn a task write into an error.
    ///
    /// <para>
    /// <paramref name="reminders"/> is optional for the same reason. Without it a
    /// task created here is stored with an EMPTY <c>reminders</c> array, and
    /// <c>ReminderWorker</c> only ever fires entries from that array — so every
    /// matter filed through the app or the chat agent is silently never reminded
    /// about. That is the state this repo was in (see docs/DIVERGENCES.md §14,
    /// "How to revert"), and it presents as a working product: the task appears,
    /// the deadline is right, and nothing arrives.
    /// </para>
    /// </summary>
    public TaskWriteService(
        TaskRepository tasks,
        Knowledge.KnowledgeService? knowledge = null,
        ReminderPlanner? reminders = null)
    {
        _tasks = tasks;
        _knowledge = knowledge;
        _reminders = reminders;
    }

    /// <summary>
    /// Write the lead-time schedule for a matter that fires.
    ///
    /// <para>
    /// Guarded on kind/dueAt rather than left to <c>ComputeRules</c> (which already
    /// returns nothing for a list item) purely to save a round trip per dateless
    /// task. <c>SetRulesRemindersAsync</c> never throws — a scheduling hiccup must
    /// not fail the write that triggered it.
    /// </para>
    /// </summary>
    private Task PlanRemindersAsync(TaskDocument? task, DateTime now, CancellationToken cancellationToken) =>
        _reminders is null || task is null || task.Kind != "reminder" || !task.DueAt.HasValue
            ? Task.CompletedTask
            : _reminders.SetRulesRemindersAsync(task, now, cancellationToken);

    /// <summary>Title plus notes — what a user would search for.</summary>
    private static string IndexableText(TaskDocument task) =>
        string.IsNullOrWhiteSpace(task.Notes) ? task.Title : $"{task.Title}\n\n{task.Notes}";

    private Task IndexAsync(TaskDocument task, CancellationToken cancellationToken) =>
        _knowledge is null
            ? Task.CompletedTask
            : _knowledge.IngestAsync(
                task.UserId,
                DAL.Features.Knowledge.ContentChunkVocabulary.TaskSource,
                task.Id,
                IndexableText(task),
                cancellationToken);

    // ---- Create -----------------------------------------------------------

    public async Task<TaskDocument> CreateAsync(
        ObjectId userId,
        TaskCreateInput input,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        // Derive when unspecified: a DATED manual task fires (reminder), a dateless
        // one is a passive list item. Mirrors the chat tool's derivation.
        var kind = input.Kind ?? (input.DueAt.HasValue ? "reminder" : "list");

        var task = new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Title = input.Title,
            Domain = input.Domain,
            Kind = kind,
            Status = "open",
            Priority = input.Priority ?? "normal",
            Subtasks = new List<SubtaskDocument>(),
            Tags = input.Tags?.ToList() ?? new List<string>(),
            DueAt = input.DueAt,
            Notes = input.Notes,
            Estimate = input.Estimate,
            Amount = input.Amount,
            SourceVoiceNoteId = input.SourceVoiceNoteId,
            Reminders = new List<ReminderEntryDocument>(),
            RescheduleCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        EnforceReminderHasDue(task);

        await _tasks.InsertAsync(task, cancellationToken).ConfigureAwait(false);

        // AFTER the insert: the planner writes the schedule with an UpdateOne keyed
        // on the task's own _id, so the row has to exist. It also assigns
        // task.Reminders in place, which is what makes the returned document carry
        // the schedule the caller just earned.
        await PlanRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
        await IndexAsync(task, cancellationToken).ConfigureAwait(false);
        return task;
    }

    // ---- Patch ------------------------------------------------------------

    /// <summary>
    /// Apply a sparse patch.
    /// </summary>
    /// <param name="patch">
    /// Only the keys the request actually carried. A BSON <b>null</b> value means
    /// the caller sent an explicit <c>null</c> and the field must be
    /// <c>$unset</c>; a key that is simply absent is not touched. This is the same
    /// convention <c>BulkService.ToMongoOps</c> reads, which is why the translation
    /// is shared rather than rewritten here.
    /// </param>
    public async Task<TaskDocument?> PatchAsync(
        ObjectId userId,
        ObjectId id,
        BsonDocument patch,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        var existing = await _tasks.FindLiveAsync(userId, id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var effective = new BsonDocument(patch);

        // Track the completion timestamp on a status transition.
        if (effective.TryGetValue("status", out var status) && !status.IsBsonNull)
        {
            effective["completedAt"] = status.AsString == "done" ? now : BsonNull.Value;
        }

        // Mongoose's `timestamps: true` stamps updatedAt on findOneAndUpdate too —
        // including for an empty patch, which is why PATCH {} still bumps it.
        effective["updatedAt"] = now;

        var ops = BulkService.ToMongoOps(effective);

        // The count only pushes BACK. Pulling a task forward is the user taking it
        // seriously; shunting it later is the slip signal that drives "what's
        // slipping" and the reminder-fatigue nudge.
        if (patch.TryGetValue("dueAt", out var raw)
            && !raw.IsBsonNull
            && existing.DueAt.HasValue
            && raw.ToUniversalTime() > existing.DueAt.Value)
        {
            ops["$inc"] = new BsonDocument("rescheduleCount", 1);
        }

        var updated = await _tasks.UpdateLiveAsync(userId, id, ops, cancellationToken).ConfigureAwait(false);

        // Reschedule ONLY when the thing the schedule is derived from moved.
        //
        // SetRulesRemindersAsync overwrites `reminders` wholesale and clears
        // `firedAt`, so calling it on an unrelated edit (a title fix, a priority
        // bump, a completion) re-arms reminders that have ALREADY gone out and the
        // user is notified a second time. Hence the narrow guard rather than
        // "replan on every patch".
        //
        // Snooze is checked first and is deliberately a different call: a snoozed
        // matter fires ONCE, at the snooze moment. Running the rules table over it
        // instead would resurrect the original lead-time schedule — the opposite of
        // what snoozing asked for. This is the path the notification action button
        // takes (status: 'snoozed' + snoozedUntil, see useNotificationActions.ts).
        if (updated is not null && _reminders is not null)
        {
            if (patch.Contains("snoozedUntil") && updated.SnoozedUntil.HasValue)
            {
                await _reminders.SetSnoozeReminderAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            else if (patch.Contains("dueAt") || patch.Contains("kind"))
            {
                await PlanRemindersAsync(updated, now, cancellationToken).ConfigureAwait(false);
            }
        }

        // Re-index on the way out, not just on create.
        //
        // The indexed text is title + notes, so an edit to either leaves the OLD
        // wording in contentChunks — and a stale chunk is worse than a missing one:
        // retrieval answers with the title the user just replaced, and duplicate
        // detection compares new matters against text that no longer exists
        // anywhere. Only re-embed when the indexed fields actually moved, since
        // every call costs an embedding request.
        if (updated is not null && TouchesIndexedText(patch))
        {
            await IndexAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>
    /// Does this patch change what <see cref="IndexableText"/> reads?
    ///
    /// <para>
    /// Status, priority, tags and dates are not embedded, so a snooze or a
    /// completion must not spend an embedding call re-encoding identical text.
    /// </para>
    /// </summary>
    private static bool TouchesIndexedText(BsonDocument patch) =>
        patch.Contains("title") || patch.Contains("notes");

    // ---- Subtasks ---------------------------------------------------------

    /// <summary>
    /// Every subtask endpoint returns the WHOLE parent task, and every one of them
    /// goes through <c>task.save()</c> in Node — which is what makes them the three
    /// endpoints that break forever on a reminder whose <c>dueAt</c> was cleared.
    /// </summary>
    public async Task<TaskDocument> AddSubtaskAsync(
        ObjectId userId,
        ObjectId id,
        string text,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;
        var task = await RequireLiveAsync(userId, id, cancellationToken).ConfigureAwait(false);

        if (task.Subtasks.Count >= TaskVocabulary.MaxSubtasks)
        {
            throw AppException.BadRequest(
                "subtask_limit",
                $"This task already has {TaskVocabulary.MaxSubtasks} subtasks — remove some before adding more.");
        }

        var subtasks = task.Subtasks.ToList();
        subtasks.Add(new SubtaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            Text = text,
            Done = false,
            CreatedAt = now,
        });

        return await SaveSubtasksAsync(userId, id, task, subtasks, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskDocument> UpdateSubtaskAsync(
        ObjectId userId,
        ObjectId id,
        ObjectId subtaskId,
        string? text,
        bool? done,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;
        var task = await RequireLiveAsync(userId, id, cancellationToken).ConfigureAwait(false);

        var subtasks = task.Subtasks.ToList();
        var index = subtasks.FindIndex(s => s.Id == subtaskId);
        if (index < 0)
        {
            throw AppException.NotFound("subtask_not_found", "Subtask no longer exists.");
        }

        var target = subtasks[index];
        subtasks[index] = new SubtaskDocument
        {
            Id = target.Id,
            Text = text ?? target.Text,
            Done = done ?? target.Done,
            CreatedAt = target.CreatedAt,
        };

        return await SaveSubtasksAsync(userId, id, task, subtasks, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaskDocument> DeleteSubtaskAsync(
        ObjectId userId,
        ObjectId id,
        ObjectId subtaskId,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;
        var task = await RequireLiveAsync(userId, id, cancellationToken).ConfigureAwait(false);

        var subtasks = task.Subtasks.Where(s => s.Id != subtaskId).ToList();
        if (subtasks.Count == task.Subtasks.Count)
        {
            throw AppException.NotFound("subtask_not_found", "Subtask no longer exists.");
        }

        return await SaveSubtasksAsync(userId, id, task, subtasks, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskDocument> SaveSubtasksAsync(
        ObjectId userId,
        ObjectId id,
        TaskDocument task,
        List<SubtaskDocument> subtasks,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // This is `task.save()`, so the document-level validators run — the ones
        // findOneAndUpdate skips. A reminder whose dueAt was cleared by
        // `PATCH {"dueAt": null}` fails here, every time, forever.
        EnforceReminderHasDue(task);

        var saved = await _tasks
            .ReplaceSubtasksAsync(userId, id, subtasks, now, cancellationToken)
            .ConfigureAwait(false);

        if (saved is null)
        {
            throw AppException.NotFound("task_not_found", "Task no longer exists.");
        }

        return saved;
    }

    private async Task<TaskDocument> RequireLiveAsync(
        ObjectId userId,
        ObjectId id,
        CancellationToken cancellationToken)
    {
        var task = await _tasks.FindLiveAsync(userId, id, cancellationToken).ConfigureAwait(false);
        return task ?? throw AppException.NotFound("task_not_found", "Task no longer exists.");
    }

    /// <summary>
    /// The reminder invariant from <c>TaskSchema.pre('validate')</c>: a matter that
    /// exists to FIRE must have a moment to fire at. A dateless reminder is a
    /// silently-broken promise.
    ///
    /// <para>
    /// Runs on create and on save — NOT on <c>findOneAndUpdate</c>, which is
    /// precisely the gap that lets <c>PATCH {"dueAt": null}</c> poison a reminder.
    /// </para>
    /// </summary>
    private static void EnforceReminderHasDue(TaskDocument task)
    {
        if (task.Kind == "reminder" && !task.DueAt.HasValue)
        {
            throw new TaskDocumentInvalidException("Task validation failed: dueAt: A reminder must have a dueAt.");
        }
    }
}
