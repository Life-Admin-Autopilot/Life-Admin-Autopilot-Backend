using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.VoiceNotes;

/// <summary>
/// Every read and write the voice-note slice makes against <c>voicenotes</c>.
///
/// <para>
/// No <c>NotDeleted()</c> anywhere: voice notes carry no <c>deletedAt</c> — the
/// audio goes with the row, so there is nothing a soft delete could restore.
/// </para>
/// </summary>
public interface IVoiceNoteRepository
{
    /// <summary>
    /// <c>findOne({_id, userId})</c>. Owner-scoped, so another user's id is
    /// indistinguishable from a missing one — the anti-enumeration choice Node
    /// makes on every one of these routes.
    /// </summary>
    Task<VoiceNoteDocument?> FindForUserAsync(
        ObjectId id,
        ObjectId userId,
        CancellationToken cancellationToken = default);

    /// <summary><c>find({userId}).sort({createdAt:-1}).limit(50)</c> — the hard-coded list.</summary>
    Task<IReadOnlyList<VoiceNoteDocument>> ListForUserAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default);

    Task InsertAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default);

    /// <summary>Whole-document write, mirroring Mongoose's <c>doc.save()</c>.</summary>
    Task SaveAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default);

    /// <summary>Every note a user owns, projected to just the storage keys, for the erasure cascade.</summary>
    Task<IReadOnlyList<string>> ListStorageKeysAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claim the next due note, or reclaim one whose lock expired after
    /// a crash.
    ///
    /// <para>
    /// The claim ONLY takes the lock — it never touches <c>attempts</c>. The retry
    /// counter is the FAILURE budget, not a claim counter: incrementing it here
    /// would let a crash or lock-expiry reclaim silently burn a retry, and a note
    /// could reach <c>failed</c> without ever genuinely failing
    /// <c>maxAttempts</c> times.
    /// </para>
    /// </summary>
    Task<VoiceNoteDocument?> ClaimNextAsync(
        DateTime now,
        TimeSpan lockFor,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVoiceNoteRepository"/>
public sealed class VoiceNoteRepository : MongoRepositoryBase<VoiceNoteDocument>, IVoiceNoteRepository
{
    /// <summary>Node's hard-coded page size. There is no pagination on this route at all.</summary>
    public const int ListLimit = 50;

    public VoiceNoteRepository(IMongoDatabase database)
        : base(database, MongoCollections.VoiceNotes)
    {
    }

    public async Task<VoiceNoteDocument?> FindForUserAsync(
        ObjectId id,
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.And(Filter.Eq(n => n.Id, id), UserScoped(userId)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<VoiceNoteDocument>> ListForUserAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(UserScoped(userId))
            .Sort(Sort.Descending(n => n.CreatedAt))
            .Limit(ListLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task InsertAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default) =>
        Collection.InsertOneAsync(note, cancellationToken: cancellationToken);

    public Task SaveAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default)
    {
        // Hand-stamped. Mongoose's `timestamps: true` adds `updatedAt` to the write
        // itself; the .NET driver does neither that nor `__v`, so both are the
        // caller's job on every save.
        note.UpdatedAt = DateTime.UtcNow;
        return Collection.ReplaceOneAsync(
            Filter.Eq(n => n.Id, note.Id),
            note,
            new ReplaceOptions { IsUpsert = false },
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListStorageKeysAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(UserScoped(userId))
            .Project(n => n.StorageKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<VoiceNoteDocument?> ClaimNextAsync(
        DateTime now,
        TimeSpan lockFor,
        CancellationToken cancellationToken = default)
    {
        var claimable = Filter.And(
            Filter.In(n => n.Status, VoiceNoteVocabulary.ClaimableStatuses),
            Filter.Lte(n => n.NextRunAt, now),
            Filter.Or(
                // `Eq(null)` matches a stored null AND a missing field, which is what
                // makes this work across both writers: Mongoose stores an explicit
                // null (`default: null`), the .NET driver omits the element.
                Filter.Eq(n => n.LockedUntil, (DateTime?)null),
                Filter.Lte(n => n.LockedUntil, now)));

        return await Collection
            .FindOneAndUpdateAsync(
                claimable,
                Update.Set(n => n.LockedUntil, now.Add(lockFor)),
                new FindOneAndUpdateOptions<VoiceNoteDocument>
                {
                    Sort = Sort.Ascending(n => n.NextRunAt),
                    ReturnDocument = ReturnDocument.After,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
