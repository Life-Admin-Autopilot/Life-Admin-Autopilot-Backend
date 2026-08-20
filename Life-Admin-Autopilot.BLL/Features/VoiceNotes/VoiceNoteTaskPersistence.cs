using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// One item on its way to becoming a Task. The shape all three callers reduce to —
/// the worker's auto-save lane, the review commit's accepts, and the provisional
/// task staged behind a clarification.
/// </summary>
public sealed record VoiceTaskSeed(
    string Key,
    string Title,
    string Domain,
    string Priority,
    string Confidence,
    TaskEstimateDocument? Estimate = null,
    DateTime? DueAt = null,
    string? Notes = null);

/// <summary>
/// Port of <c>modules/ai/voiceCore/persist.ts</c>: upsert one Task per
/// <c>(note, item key)</c>.
///
/// <para>
/// <b>Not routed through <c>BulkService</c>, deliberately</b> — the same call
/// <c>DocumentScanReviewService</c> already documents. The kernel rule is that
/// multi-task MUTATIONS go through the journal so undo works; this is a CREATE,
/// and Node writes it with a plain <c>Task.bulkWrite</c> of upserts and no journal
/// entry. Adding one would put a <c>TaskBulkOp</c> row on the .NET side that the
/// reference server does not have, and offer an undo it does not offer.
/// </para>
/// </summary>
public interface IVoiceNoteTaskPersistence
{
    /// <summary>
    /// Idempotent per <c>(note, item key)</c>. The partial unique index on
    /// <c>{userId, sourceVoiceNoteId, sourceTaskKey}</c> is what makes a worker
    /// reclaim — or a double-tapped accept — a no-op instead of a duplicate Task,
    /// which is why <c>VoiceNoteIndexes</c> declares it.
    /// </summary>
    /// <returns>
    /// The resulting Tasks, created or pre-existing, in ITEM order — the response's
    /// <c>tasks</c> array lines up with the review card, not with Mongo's insertion
    /// order.
    /// </returns>
    Task<IReadOnlyList<TaskDocument>> PersistAsync(
        ObjectId userId,
        ObjectId voiceNoteId,
        IReadOnlyList<VoiceTaskSeed> items,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVoiceNoteTaskPersistence"/>
public sealed class VoiceNoteTaskPersistence : IVoiceNoteTaskPersistence
{
    private readonly IMongoCollection<TaskDocument> _tasks;
    private readonly IMongoCollection<BsonDocument> _rawTasks;
    private readonly Kernel.Reminders.ReminderPlanner? _reminders;

    /// <summary>
    /// <paramref name="reminders"/> is optional so the existing tests that build
    /// this from a bare database keep compiling. Without it a matter dictated into
    /// a voice note is stored with an empty <c>reminders</c> array and never fires
    /// — see the note on <c>TaskWriteService</c>'s constructor.
    /// </summary>
    public VoiceNoteTaskPersistence(IMongoDatabase database, Kernel.Reminders.ReminderPlanner? reminders = null)
    {
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
        _rawTasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
        _reminders = reminders;
    }

    public async Task<IReadOnlyList<TaskDocument>> PersistAsync(
        ObjectId userId,
        ObjectId voiceNoteId,
        IReadOnlyList<VoiceTaskSeed> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return Array.Empty<TaskDocument>();
        }

        var now = DateTime.UtcNow;
        var writes = items
            .Select(item => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                RawIdentity(userId, voiceNoteId, item.Key),
                new BsonDocument("$setOnInsert", SeedTask(userId, voiceNoteId, item, now)))
            {
                IsUpsert = true,
            })
            .ToList();

        await _rawTasks
            .BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
            .ConfigureAwait(false);

        var keys = items.Select(i => i.Key).ToList();
        var found = await _tasks
            .Find(Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
                Builders<TaskDocument>.Filter.Eq(t => t.SourceVoiceNoteId, voiceNoteId),
                Builders<TaskDocument>.Filter.In(t => t.SourceTaskKey, keys)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byKey = found
            .Where(t => t.SourceTaskKey is not null)
            .GroupBy(t => t.SourceTaskKey!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Schedule only what has never been scheduled.
        //
        // The write above is an idempotent upsert keyed on sourceTaskKey, so a
        // re-run of the same voice note returns rows that already exist — and
        // SetRulesRemindersAsync clears firedAt, so replanning one of those would
        // fire a reminder the user has already had. An empty reminders array is the
        // reliable "never planned" signal, because the planner always writes at
        // least the at-due entry for a dated reminder.
        if (_reminders is not null)
        {
            foreach (var task in found)
            {
                if (task.Kind == "reminder" && task.DueAt.HasValue && task.Reminders.Count == 0)
                {
                    await _reminders.SetRulesRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return items
            .Select(i => byKey.TryGetValue(i.Key, out var task) ? task : null)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
    }

    private static FilterDefinition<BsonDocument> RawIdentity(ObjectId userId, ObjectId voiceNoteId, string key) =>
        Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", userId),
            Builders<BsonDocument>.Filter.Eq("sourceVoiceNoteId", voiceNoteId),
            Builders<BsonDocument>.Filter.Eq("sourceTaskKey", key));

    /// <summary>
    /// The fields Node's <c>$setOnInsert</c> writes, PLUS the schema defaults
    /// Mongoose 8 adds for free (<c>setDefaultsOnInsert</c> is on by default), PLUS
    /// <c>__v</c>, which the .NET driver never stamps.
    ///
    /// <para>
    /// The optional three are conditional exactly as they are in Node — a JS
    /// truthiness spread, so an empty <c>notes</c> is omitted rather than stored.
    /// Writing <c>dueAt: null</c> where Node omits the key would make the Task carry
    /// a due date that is present-and-empty rather than absent.
    /// </para>
    ///
    /// <para>
    /// <c>$setOnInsert</c> is also what protects a hand-edited estimate: a worker
    /// retry simply does not write the row again.
    /// </para>
    /// </summary>
    private static BsonDocument SeedTask(
        ObjectId userId,
        ObjectId voiceNoteId,
        VoiceTaskSeed item,
        DateTime now)
    {
        var seed = new BsonDocument
        {
            ["userId"] = userId,
            ["title"] = item.Title,
            ["domain"] = item.Domain,
            ["priority"] = item.Priority,
            ["status"] = "open",
            ["sourceVoiceNoteId"] = voiceNoteId,
            ["sourceTaskKey"] = item.Key,
            ["confidence"] = item.Confidence,

            // Mongoose schema defaults, applied on upsert-insert.
            ["kind"] = "list",
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(),
            ["rescheduleCount"] = 0,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        if (item.Estimate is not null)
        {
            seed["estimate"] = new BsonDocument
            {
                ["minMinutes"] = item.Estimate.MinMinutes,
                ["maxMinutes"] = item.Estimate.MaxMinutes,
                ["source"] = item.Estimate.Source,
            };
        }

        if (item.DueAt is not null)
        {
            seed["dueAt"] = item.DueAt.Value;
        }

        if (!string.IsNullOrEmpty(item.Notes))
        {
            seed["notes"] = item.Notes;
        }

        return seed;
    }
}
