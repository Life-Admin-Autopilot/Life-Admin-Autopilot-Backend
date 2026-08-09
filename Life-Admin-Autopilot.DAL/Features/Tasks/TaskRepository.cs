using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Tasks;

/// <summary>
/// Every single-task read and write the Matters slice performs.
///
/// <para>
/// Composes <see cref="MongoRepositoryBase{TDocument}.NotDeleted"/> on every live
/// read, per KERNEL.md §7 — a forgotten soft-delete clause silently resurrects
/// trashed rows. Multi-task mutations do NOT live here: they go through the
/// kernel's <c>BulkService</c>, which is the only journaled write path.
/// </para>
/// </summary>
public sealed class TaskRepository : MongoRepositoryBase<TaskDocument>
{
    public TaskRepository(IMongoDatabase database)
        : base(database, MongoCollections.Tasks)
    {
    }

    /// <summary>The tasks collection, for the kernel services that take it directly.</summary>
    public IMongoCollection<TaskDocument> Tasks => Collection;

    public Task<TaskDocument?> FindLiveAsync(ObjectId userId, ObjectId id, CancellationToken ct = default) =>
        Collection
            .Find(Filter.And(Filter.Eq(t => t.Id, id), LiveForUser(userId)))
            .FirstOrDefaultAsync(ct)!;

    public Task InsertAsync(TaskDocument task, CancellationToken ct = default) =>
        Collection.InsertOneAsync(task, cancellationToken: ct);

    /// <summary>
    /// <c>findOneAndUpdate({..., notDeleted}, ops, {new: true})</c>. Returns null
    /// when the row vanished between the read and the write.
    /// </summary>
    public Task<TaskDocument?> UpdateLiveAsync(
        ObjectId userId,
        ObjectId id,
        BsonDocument ops,
        CancellationToken ct = default) =>
        Collection
            .FindOneAndUpdateAsync<TaskDocument>(
                Filter.And(Filter.Eq(t => t.Id, id), LiveForUser(userId)),
                new BsonDocumentUpdateDefinition<TaskDocument>(ops),
                new FindOneAndUpdateOptions<TaskDocument> { ReturnDocument = ReturnDocument.After },
                ct)!;

    /// <summary>
    /// Persist the subtask array and bump <c>updatedAt</c>, mirroring what
    /// <c>task.save()</c> writes after a subtask mutation.
    /// </summary>
    public Task<TaskDocument?> ReplaceSubtasksAsync(
        ObjectId userId,
        ObjectId id,
        IReadOnlyList<SubtaskDocument> subtasks,
        DateTime now,
        CancellationToken ct = default) =>
        Collection
            .FindOneAndUpdateAsync<TaskDocument>(
                Filter.And(Filter.Eq(t => t.Id, id), LiveForUser(userId)),
                Update.Set(t => t.Subtasks, subtasks.ToList()).Set(t => t.UpdatedAt, now),
                new FindOneAndUpdateOptions<TaskDocument> { ReturnDocument = ReturnDocument.After },
                ct)!;

    // ---- Trash ------------------------------------------------------------

    /// <summary>
    /// The Trash view: newest-deleted first, capped at 200. Note the predicate is
    /// the INVERSE of <c>NotDeleted()</c> — this is the one read that wants the
    /// soft-deleted rows.
    /// </summary>
    public Task<List<TaskDocument>> FindTrashedAsync(ObjectId userId, CancellationToken ct = default) =>
        Collection
            .Find(Filter.And(UserScoped(userId), Filter.Exists("deletedAt", true)))
            .Sort(Sort.Descending(t => t.DeletedAt))
            .Limit(200)
            .ToListAsync(ct);

    /// <summary>
    /// The one genuinely irreversible operation in the app — a hard
    /// <c>deleteMany</c>, reachable only from an explicit "Empty trash".
    /// </summary>
    public async Task<long> PurgeTrashAsync(ObjectId userId, CancellationToken ct = default)
    {
        var result = await Collection
            .DeleteManyAsync(Filter.And(UserScoped(userId), Filter.Exists("deletedAt", true)), ct)
            .ConfigureAwait(false);

        return result.DeletedCount;
    }

    /// <summary>Lifts a row back out of the trash by <c>$unset</c>-ing <c>deletedAt</c>.</summary>
    public Task<TaskDocument?> RestoreAsync(ObjectId userId, ObjectId id, CancellationToken ct = default) =>
        Collection
            .FindOneAndUpdateAsync<TaskDocument>(
                Filter.And(
                    Filter.Eq(t => t.Id, id),
                    UserScoped(userId),
                    Filter.Exists("deletedAt", true)),
                Update.Unset("deletedAt"),
                new FindOneAndUpdateOptions<TaskDocument> { ReturnDocument = ReturnDocument.After },
                ct)!;

    // ---- Tags -------------------------------------------------------------

    /// <summary>
    /// Distinct tags across the user's live rows, sorted the way JavaScript's
    /// bare <c>Array#sort()</c> does — by UTF-16 code unit, which is
    /// <see cref="StringComparer.Ordinal"/>, NOT a culture-aware compare.
    /// </summary>
    public async Task<List<string>> DistinctTagsAsync(ObjectId userId, CancellationToken ct = default)
    {
        var tags = await Collection
            .DistinctAsync<string>("tags", LiveForUser(userId), cancellationToken: ct)
            .ConfigureAwait(false);

        var all = await tags.ToListAsync(ct).ConfigureAwait(false);
        all.Sort(StringComparer.Ordinal);
        return all;
    }
}
