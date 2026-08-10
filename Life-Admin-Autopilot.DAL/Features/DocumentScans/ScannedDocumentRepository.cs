using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.DocumentScans;

/// <summary>
/// Every read and write the document-scan slice makes against
/// <c>scanneddocuments</c>.
///
/// <para>
/// No <c>NotDeleted()</c> anywhere: scans are HARD deleted (the bytes go with the
/// row, so there is nothing a soft delete could restore), which is why the
/// collection carries no <c>deletedAt</c> to compose.
/// </para>
/// </summary>
public interface IScannedDocumentRepository
{
    /// <summary>
    /// <c>findOne({_id, userId})</c>. Owner-scoped, so another user's id is
    /// indistinguishable from a missing one — a deliberate anti-enumeration choice
    /// that Node makes on every one of these routes.
    /// </summary>
    Task<ScannedDocumentDocument?> FindForUserAsync(
        ObjectId id,
        ObjectId userId,
        CancellationToken cancellationToken = default);

    Task<ScannedDocumentDocument?> FindByIdAsync(ObjectId id, CancellationToken cancellationToken = default);

    /// <summary><c>find({userId}).sort({createdAt:-1}).limit(50)</c> — the hard-coded list.</summary>
    Task<IReadOnlyList<ScannedDocumentDocument>> ListForUserAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default);

    Task InsertAsync(ScannedDocumentDocument document, CancellationToken cancellationToken = default);

    /// <summary>Whole-document write, mirroring Mongoose's <c>doc.save()</c>.</summary>
    Task SaveAsync(ScannedDocumentDocument document, CancellationToken cancellationToken = default);

    Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default);

    /// <summary>Every scan a user owns, projected to just the storage keys, for the erasure cascade.</summary>
    Task<IReadOnlyList<string>> ListStorageKeysAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claim the next due document, or reclaim one whose lease expired
    /// after a crash. The lease stamp IS the claim — two workers cannot both win,
    /// because only one <c>findOneAndUpdate</c> can match a given row.
    /// </summary>
    Task<ScannedDocumentDocument?> ClaimNextAsync(
        DateTime now,
        TimeSpan lockFor,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IScannedDocumentRepository"/>
public sealed class ScannedDocumentRepository
    : MongoRepositoryBase<ScannedDocumentDocument>, IScannedDocumentRepository
{
    /// <summary>Node's hard-coded page size. There is no pagination on this route at all.</summary>
    public const int ListLimit = 50;

    public ScannedDocumentRepository(IMongoDatabase database)
        : base(database, MongoCollections.ScannedDocuments)
    {
    }

    public async Task<ScannedDocumentDocument?> FindForUserAsync(
        ObjectId id,
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.And(Filter.Eq(d => d.Id, id), UserScoped(userId)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<ScannedDocumentDocument?> FindByIdAsync(
        ObjectId id,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.Eq(d => d.Id, id))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ScannedDocumentDocument>> ListForUserAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(UserScoped(userId))
            .Sort(Sort.Descending(d => d.CreatedAt))
            .Limit(ListLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task InsertAsync(ScannedDocumentDocument document, CancellationToken cancellationToken = default) =>
        Collection.InsertOneAsync(document, cancellationToken: cancellationToken);

    public Task SaveAsync(ScannedDocumentDocument document, CancellationToken cancellationToken = default)
    {
        document.UpdatedAt = DateTime.UtcNow;
        return Collection.ReplaceOneAsync(
            Filter.Eq(d => d.Id, document.Id),
            document,
            new ReplaceOptions { IsUpsert = false },
            cancellationToken);
    }

    public Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        Collection.DeleteOneAsync(Filter.Eq(d => d.Id, id), cancellationToken);

    public async Task<IReadOnlyList<string>> ListStorageKeysAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(UserScoped(userId))
            .Project(d => d.StorageKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<ScannedDocumentDocument?> ClaimNextAsync(
        DateTime now,
        TimeSpan lockFor,
        CancellationToken cancellationToken = default)
    {
        var claimable = Filter.And(
            Filter.In(d => d.Status, ScannedDocumentVocabulary.ClaimableStatuses),
            Filter.Lte(d => d.NextRunAt, now),
            Filter.Or(
                // `Eq(null)` matches a stored null AND a missing field, which is what
                // makes this work across both writers: Mongoose stores an explicit
                // null (`default: null`), the .NET driver omits the element.
                Filter.Eq(d => d.LockedUntil, (DateTime?)null),
                Filter.Lte(d => d.LockedUntil, now)));

        return await Collection
            .FindOneAndUpdateAsync(
                claimable,
                Update.Set(d => d.LockedUntil, now.Add(lockFor)),
                new FindOneAndUpdateOptions<ScannedDocumentDocument>
                {
                    Sort = Sort.Ascending(d => d.NextRunAt),
                    ReturnDocument = ReturnDocument.After,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
