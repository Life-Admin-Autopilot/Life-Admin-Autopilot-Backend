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

                // A rebuild means the facts moved, so any earlier attempt was for a
                // state that no longer exists. Clearing it is what lets the next read
                // ask for a sentence about the new one. Written as an explicit null
                // rather than $unset so the field's absence never has to mean two
                // different things.
                ["proseAttemptedHash"] = BsonNull.Value,
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

    /// <summary>
    /// Record that a model was asked for this day's sentence, and store the sentence
    /// if it wrote one.
    ///
    /// <para>
    /// <b>Conditional on <paramref name="sourceHash"/>, which is the whole point.</b>
    /// The sentence is written in the background, so by the time it lands the user may
    /// already have completed, added or moved a matter — and the row will have been
    /// rebuilt against the new state. Matching the fingerprint the sentence was
    /// generated FROM means a stale one is silently dropped instead of overwriting a
    /// current headline with a description of a day that has moved on. No match, no
    /// write, no error: the plain sentence in the row is still true.
    /// </para>
    ///
    /// <para>
    /// It touches <c>payload.headline</c> and nothing else inside the payload. Every
    /// count in the row was computed from documents and stays exactly as the build
    /// left it — the model is not permitted to move a number, and an update that
    /// wrote the whole payload would make that a matter of trust rather than of shape.
    /// </para>
    /// </summary>
    /// <param name="headline">
    /// The sentence the model wrote, or NULL when it produced nothing usable. A null
    /// still records the attempt — that is the difference between "no sentence yet"
    /// and "asked, and the answer was nothing", and only the second one should stop
    /// the next read asking again.
    /// </param>
    /// <returns>True if the row was still describing the same state and was patched.</returns>
    public async Task<bool> CompleteProseAsync(
        ObjectId userId,
        string localDate,
        string sourceHash,
        string? headline,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<DailyDigestDocument>.Update
            .Set(d => d.ProseAttemptedHash, sourceHash)
            .Set(d => d.UpdatedAt, updatedAt);

        if (headline is not null)
        {
            update = update.Set(d => d.Payload.Headline, headline);
        }

        var result = await Collection.UpdateOneAsync(
            Filter.And(
                UserScoped(userId),
                Filter.Eq(d => d.LocalDate, localDate),
                Filter.Eq(d => d.SourceHash, sourceHash)),
            update,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.MatchedCount > 0;
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
