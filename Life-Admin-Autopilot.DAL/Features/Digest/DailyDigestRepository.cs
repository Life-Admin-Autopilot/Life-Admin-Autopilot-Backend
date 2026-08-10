using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Digest;

/// <summary>
/// The digest cache collection. Two operations: read today's row, and write it back.
/// </summary>
public sealed class DailyDigestRepository : MongoRepositoryBase<DailyDigestDocument>
{
    public DailyDigestRepository(IMongoDatabase database)
        : base(database, DigestCollections.DailyDigests)
    {
    }

    public Task<DailyDigestDocument?> FindAsync(
        ObjectId userId,
        string localDate,
        CancellationToken cancellationToken = default) =>
        Collection
            .Find(Filter.And(UserScoped(userId), Filter.Eq(d => d.LocalDate, localDate)))
            .FirstOrDefaultAsync(cancellationToken)!;

    /// <summary>
    /// The cache write — <c>findOneAndUpdate({userId, localDate}, {$set: …}, {upsert: true})</c>.
    ///
    /// <para>
    /// <b>The timestamps are written by hand ON PURPOSE.</b> The Node model carries
    /// <c>{ timestamps: true }</c>, and Mongoose reacts to that by injecting
    /// <c>updatedAt</c> into the <c>$set</c> document it sends and <c>createdAt</c>
    /// into a <c>$setOnInsert</c> — the application code never mentions either. The
    /// .NET driver does no such thing: it sends exactly the update you hand it. A
    /// straight transcription of the Node call therefore produces a row that is
    /// missing both fields, and the two collections stop comparing equal.
    /// </para>
    ///
    /// <para>
    /// Nothing in <c>GET /me/digest</c> reads these two fields, so this is not a
    /// response-shape bug today. It is written correctly anyway because a future
    /// reader of this collection — or a raw-document differential against the
    /// reference server — would otherwise see a difference that looks like data loss.
    /// The same omission is a genuine RESPONSE bug wherever a .NET slice writes a
    /// collection whose <c>updatedAt</c> feeds this digest's fingerprint (see
    /// <c>DailyDigestSourceState</c>).
    /// </para>
    /// </summary>
    public Task UpsertAsync(
        ObjectId userId,
        string localDate,
        string sourceHash,
        string locale,
        DateTime generatedAt,
        DailyDigestPayloadDocument payload,
        CancellationToken cancellationToken = default)
    {
        var update = new BsonDocument
        {
            ["$set"] = new BsonDocument
            {
                ["sourceHash"] = sourceHash,
                ["locale"] = locale,
                ["generatedAt"] = generatedAt,
                ["payload"] = payload.ToBsonDocument(),
                ["updatedAt"] = generatedAt,
            },
            ["$setOnInsert"] = new BsonDocument
            {
                ["createdAt"] = generatedAt,
                ["__v"] = 0,
            },
        };

        return Collection.FindOneAndUpdateAsync<DailyDigestDocument>(
            Filter.And(UserScoped(userId), Filter.Eq(d => d.LocalDate, localDate)),
            update,
            new FindOneAndUpdateOptions<DailyDigestDocument> { IsUpsert = true },
            cancellationToken);
    }
}

/// <summary>
/// This slice's collection name. Deliberately NOT added to
/// <c>MongoCollections</c> — that class is a merge-conflict magnet and the kernel
/// contract asks slices to declare their own (§7).
/// </summary>
public static class DigestCollections
{
    public const string DailyDigests = MongoCollections.DailyDigests;
}
