using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Profile;

/// <summary>
/// The export's reads. Every section except <c>user</c> is a RAW
/// <c>.lean()</c> document on the reference — <c>_id</c> (not <c>id</c>),
/// <c>__v</c> and <c>userId</c> all present, no <c>toJSON</c> transform applied —
/// so this returns <see cref="BsonDocument"/> rather than a typed document.
///
/// <para>
/// <b>That is a deliberate design choice, not laziness.</b> Six of the eleven
/// exported collections belong to slices that are not merged yet
/// (<c>voicenotes</c>, <c>aiconversations</c>, <c>aiusagecounters</c>,
/// <c>clarifications</c>, <c>dailydigests</c>). Reading them as BSON means the
/// export ships complete today and does not acquire a compile-time dependency on
/// five other slices' document types — types whose shapes must not be duplicated
/// here anyway.
/// </para>
/// </summary>
public interface IAccountExportRepository
{
    /// <param name="excludedFields">
    /// Projected out with <c>{ field: 0 }</c>, matching the reference exactly:
    /// <c>storageKey</c> on the two blob-backed collections (an internal disk path,
    /// not user data) and <c>tokenHash</c>/<c>replacedBy</c> on sessions
    /// (<c>tokenHash</c> IS the credential).
    /// </param>
    Task<IReadOnlyList<BsonDocument>> FindRawAsync(
        string collectionName,
        ObjectId userId,
        int limit,
        IReadOnlyList<string>? excludedFields = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountExportRepository"/>
public sealed class AccountExportRepository : IAccountExportRepository
{
    private readonly IMongoDatabase _database;

    public AccountExportRepository(IMongoDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// <c>Model.find({ userId }).limit(n).lean()</c> — and note what is NOT here.
    ///
    /// <para>
    /// No sort and no pagination, so which rows come back past the cap is Mongo's
    /// natural order. No <c>NotDeleted()</c> either: the reference exports
    /// soft-deleted matters along with live ones, which is right for a
    /// "everything we hold about you" download even though it is the opposite of
    /// every API read.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BsonDocument>> FindRawAsync(
        string collectionName,
        ObjectId userId,
        int limit,
        IReadOnlyList<string>? excludedFields = null,
        CancellationToken cancellationToken = default)
    {
        var find = _database
            .GetCollection<BsonDocument>(collectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .Limit(limit);

        if (excludedFields is { Count: > 0 })
        {
            var projection = new BsonDocument();
            foreach (var field in excludedFields)
            {
                projection[field] = 0;
            }

            find = find.Project<BsonDocument>(projection);
        }

        return await find.ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
