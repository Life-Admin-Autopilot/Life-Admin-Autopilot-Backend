using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Kernel.Tasks;

/// <summary>
/// Port of <c>server/src/modules/tasks/bulkService.ts</c>.
///
/// <para>
/// <b>This is the ONLY journaled write path for Tasks, and the only code allowed
/// to delete more than one.</b> Every path here writes exactly one
/// <c>TaskBulkOp</c> BEFORE touching a Task, so there is never a window where work
/// has happened that cannot be reversed. Deletion is ALWAYS soft — enforced here
/// rather than trusted to a prompt rule, because the chat agent calls this same
/// service and is therefore structurally incapable of an irreversible bulk delete.
/// </para>
///
/// <para><b>Slices must route every multi-task mutation through this.</b> A
/// hand-rolled <c>UpdateMany</c> elsewhere silently removes undo.</para>
/// </summary>
public sealed class BulkService
{
    /// <summary>
    /// Cap on one operation. Past this the user almost certainly means
    /// "everything", and a mistake at that size is not something an undo toast can
    /// console them about — the UI should make them narrow the range instead.
    /// </summary>
    public const int MaxBulkTargets = 500;

    private readonly IMongoCollection<TaskDocument> _tasks;
    private readonly IMongoCollection<TaskBulkOpDocument> _ops;
    private readonly ClarificationCascade _cascade;
    private readonly Reminders.ReminderPlanner? _reminders;

    /// <summary>
    /// <paramref name="reminders"/> is optional so the existing tests that build
    /// this by hand keep compiling.
    ///
    /// <para>
    /// It is used for SNOOZE only. None of the other actions here touch
    /// <c>dueAt</c> or <c>kind</c>, and running the rules table over a task whose
    /// deadline has not moved would clear <c>firedAt</c> and re-fire reminders that
    /// already went out — so this deliberately does NOT call
    /// <c>SetRulesRemindersAsync</c>.
    /// </para>
    /// </summary>
    public BulkService(
        IMongoDatabase database,
        ClarificationCascade cascade,
        Reminders.ReminderPlanner? reminders = null)
    {
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
        _ops = database.GetCollection<TaskBulkOpDocument>(MongoCollections.TaskBulkOps);
        _cascade = cascade;
        _reminders = reminders;
    }

    /// <summary>
    /// Resolve either an explicit id list or a filter into concrete tasks. Both
    /// paths are user-scoped and exclude already-trashed rows, so a filter can
    /// never reach beyond the caller's own live data.
    ///
    /// <para>The filter path fetches <c>MaxBulkTargets + 1</c> — one over the cap —
    /// which is how <see cref="SummarizeWarnings"/> detects truncation.</para>
    /// </summary>
    public async Task<IReadOnlyList<TaskDocument>> ResolveTargetsAsync(
        ObjectId userId,
        BulkTarget target,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (target.Ids is not null)
        {
            var ids = target.Ids.Select(MongoRepositoryBase<TaskDocument>.ParseObjectId).ToList();
            var byId = Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.In(t => t.Id, ids),
                Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
                MongoRepositoryBase<TaskDocument>.NotDeleted());

            return await _tasks.Find(byId).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        if (target.Filter is null)
        {
            throw AppException.BadRequest("invalid_body", "Provide ids or a filter.");
        }

        var built = TaskQuery.BuildFilter(userId, target.Filter, now);
        return await _tasks
            .Find(new BsonDocumentFilterDefinition<TaskDocument>(built))
            .Limit(MaxBulkTargets + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static BulkWarnings SummarizeWarnings(IReadOnlyList<TaskDocument> tasks) => new(
        FromDocuments: tasks.Count(t => t.SourceDocumentId is not null),
        RemindersFired: tasks.Count(t => t.Reminders.Any(r => r.FiredAt is not null)),
        Truncated: tasks.Count > MaxBulkTargets);

    /// <summary>
    /// Split a sparse patch into the <c>$set</c>/<c>$unset</c> halves Mongo needs.
    ///
    /// <para>
    /// <b>Public, and shared on purpose.</b> The categorize flow applies a reviewed
    /// proposal rather than an action, and it MUST use this same function: undo
    /// reads <c>prior</c> back through the same translation, so a second
    /// implementation that treated BSON null differently would make categorize runs
    /// un-undoable in a way nothing would catch until someone tried.
    /// </para>
    /// </summary>
    public static BsonDocument ToMongoOps(BsonDocument patch)
    {
        var set = new BsonDocument();
        var unset = new BsonDocument();

        foreach (var element in patch)
        {
            if (element.Value.IsBsonNull)
            {
                unset[element.Name] = 1;
            }
            else
            {
                set[element.Name] = element.Value;
            }
        }

        var ops = new BsonDocument();
        if (set.ElementCount > 0)
        {
            ops["$set"] = set;
        }

        if (unset.ElementCount > 0)
        {
            ops["$unset"] = unset;
        }

        return ops;
    }

    /// <summary>
    /// Build the before/after patch pair for one task under one action, or null
    /// when the action is a no-op on that task.
    ///
    /// <para><c>prior</c> uses the null convention: an explicit BSON null means the
    /// field was ABSENT and must be <c>$unset</c> on undo. Recording it as simply
    /// missing would strand the field at its post-op value.</para>
    /// </summary>
    private static (BsonDocument Prior, BsonDocument Next)? PatchFor(
        TaskDocument task,
        BulkActionInput input,
        DateTime now)
    {
        switch (input)
        {
            case BulkActionInput.Delete:
                return (
                    new BsonDocument { ["deletedAt"] = BsonNull.Value },
                    new BsonDocument { ["deletedAt"] = now });

            case BulkActionInput.Complete:
                if (task.Status == "done")
                {
                    return null;
                }

                return (
                    new BsonDocument
                    {
                        ["status"] = task.Status,
                        ["completedAt"] = task.CompletedAt.HasValue
                            ? task.CompletedAt.Value
                            : BsonNull.Value,
                    },
                    new BsonDocument { ["status"] = "done", ["completedAt"] = now });

            case BulkActionInput.Snooze snooze:
                return (
                    new BsonDocument
                    {
                        ["status"] = task.Status,
                        ["snoozedUntil"] = task.SnoozedUntil.HasValue
                            ? task.SnoozedUntil.Value
                            : BsonNull.Value,
                    },
                    new BsonDocument { ["status"] = "snoozed", ["snoozedUntil"] = snooze.Until });

            case BulkActionInput.SetDomain setDomain:
                if (task.Domain == setDomain.Domain)
                {
                    return null;
                }

                return (
                    new BsonDocument { ["domain"] = task.Domain },
                    new BsonDocument { ["domain"] = setDomain.Domain });

            case BulkActionInput.AddTags addTags:
            {
                var additions = addTags.Tags
                    .Select(TaskVocabulary.NormalizeTag)
                    .Where(t => t is not null)
                    .Select(t => t!)
                    .ToList();

                var merged = task.Tags
                    .Concat(additions)
                    .Distinct()
                    .Take(TaskVocabulary.MaxTags)
                    .ToList();

                if (merged.Count == task.Tags.Count && additions.All(t => task.Tags.Contains(t)))
                {
                    return null;
                }

                return (
                    new BsonDocument { ["tags"] = new BsonArray(task.Tags) },
                    new BsonDocument { ["tags"] = new BsonArray(merged) });
            }

            default:
                return null;
        }
    }

    public async Task<BulkResult> ApplyAsync(
        ObjectId userId,
        BulkTarget target,
        BulkActionInput input,
        string? label = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var at = now ?? DateTime.UtcNow;
        var tasks = await ResolveTargetsAsync(userId, target, at, cancellationToken).ConfigureAwait(false);
        var warnings = SummarizeWarnings(tasks);

        if (warnings.Truncated)
        {
            throw AppException.BadRequest(
                "bulk_too_large",
                $"That matches more than {MaxBulkTargets} matters. Narrow the range and try again.");
        }

        var entries = new List<BulkOpEntryDocument>();
        foreach (var task in tasks)
        {
            var patch = PatchFor(task, input, at);

            // A no-op (already done, already in that domain) is SKIPPED rather than
            // recorded, so undo can't "restore" a change that never happened.
            if (patch is null)
            {
                continue;
            }

            entries.Add(new BulkOpEntryDocument
            {
                TaskId = task.Id,
                Prior = patch.Value.Prior,
                Next = patch.Value.Next,
            });
        }

        if (entries.Count == 0)
        {
            return new BulkResult(0, null, warnings);
        }

        // The op record is written FIRST. If the bulk write fails halfway, the
        // record still describes what was intended and undo restores whatever
        // landed.
        var op = new TaskBulkOpDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Kind = "bulk",
            Action = input.Action,
            Status = "applied",
            Entries = entries,
            Label = label,
            AppliedAt = at,
            ExpiresAt = BulkOpVocabulary.Expiry(at),
            CreatedAt = at,
            UpdatedAt = at,
        };
        await _ops.InsertOneAsync(op, cancellationToken: cancellationToken).ConfigureAwait(false);

        await _tasks
            .BulkWriteAsync(
                entries.Select(e => new UpdateOneModel<TaskDocument>(
                    Builders<TaskDocument>.Filter.And(
                        Builders<TaskDocument>.Filter.Eq(t => t.Id, e.TaskId),
                        Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId)),
                    new BsonDocumentUpdateDefinition<TaskDocument>(ToMongoOps(e.Next)))),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // A snoozed matter fires ONCE, at the snooze moment — that is the point of
        // snooze, and it is the only action here that changes when a reminder is
        // due. Without this the bulk path moves the badge and the list position
        // while the ORIGINAL schedule stays armed, so the phone still buzzes at the
        // old time for something the user just pushed away.
        //
        // SnoozedUntil is assigned in memory rather than re-read: the bulk write
        // above already committed this exact value, and the planner needs only that
        // and the id.
        if (_reminders is not null && input is BulkActionInput.Snooze snoozed)
        {
            var touched = entries.Select(e => e.TaskId).ToHashSet();
            foreach (var task in tasks.Where(t => touched.Contains(t.Id)))
            {
                task.SnoozedUntil = snoozed.Until;
                await _reminders.SetSnoozeReminderAsync(task, cancellationToken).ConfigureAwait(false);
            }
        }

        // A question is ABOUT a task. Delete the task and the question is moot —
        // leaving it open strands a prompt the user can no longer answer, which is
        // what made "Questions for you" outlive everything the user cleared.
        if (input is BulkActionInput.Delete)
        {
            await _cascade
                .DropForTasksAsync(userId, entries.Select(e => e.TaskId).ToList(), cancellationToken)
                .ConfigureAwait(false);
        }

        return new BulkResult(entries.Count, op.Id.ToString(), warnings);
    }

    /// <summary>
    /// Reverse an applied op.
    ///
    /// <para>
    /// <b>Deliberate asymmetry:</b> undo restores the tasks but does NOT un-drop the
    /// clarifications the delete cascaded. A dropped question is a settled
    /// conversation; resurrecting it would re-ask something the user has already
    /// moved past. Node behaves the same way — this is ported on purpose, not an
    /// oversight to be "fixed".
    /// </para>
    ///
    /// <para>Idempotent: undoing twice is a no-op, not an error. A double-tap on the
    /// toast must not surface a failure for work that is already reversed.</para>
    /// </summary>
    public async Task<int> UndoAsync(
        ObjectId userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(token, out var opId))
        {
            throw AppException.NotFound("undo_not_found", "That change is no longer undoable.");
        }

        var op = await _ops
            .Find(Builders<TaskBulkOpDocument>.Filter.And(
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Id, opId),
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.UserId, userId)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (op is null)
        {
            throw AppException.NotFound(
                "undo_not_found",
                $"That change is no longer undoable — undo is available for {BulkOpVocabulary.TtlDays} days.");
        }

        if (op.Status != "applied")
        {
            return 0;
        }

        if (op.Entries.Count > 0)
        {
            await _tasks
                .BulkWriteAsync(
                    op.Entries.Select(e => new UpdateOneModel<TaskDocument>(
                        Builders<TaskDocument>.Filter.And(
                            Builders<TaskDocument>.Filter.Eq(t => t.Id, e.TaskId),
                            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId)),
                        new BsonDocumentUpdateDefinition<TaskDocument>(ToMongoOps(e.Prior)))),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        await _ops
            .UpdateOneAsync(
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Id, op.Id),
                Builders<TaskBulkOpDocument>.Update
                    .Set(o => o.Status, "undone")
                    .Set(o => o.UndoneAt, DateTime.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return op.Entries.Count;
    }
}
