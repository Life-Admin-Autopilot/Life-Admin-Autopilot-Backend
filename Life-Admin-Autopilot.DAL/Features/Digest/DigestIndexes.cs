using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Digest;

/// <summary>
/// The two indexes declared on <c>models/DailyDigest.ts</c>. Mongoose creates them
/// from the schema; the .NET driver creates nothing, so they are declared here.
///
/// <para>
/// Neither is an optimisation.
/// </para>
///
/// <list type="bullet">
///   <item>
///     The UNIQUE <c>{userId, localDate}</c> index is the uniqueness guarantee
///     behind the upsert: one digest per user per local day. Without it two
///     concurrent dashboard loads both insert, and the user then gets whichever of
///     two rows Mongo happens to return — including, intermittently, a
///     <c>generatedAt</c> that goes backwards.
///   </item>
///   <item>
///     The TTL index on <c>generatedAt</c> is what keeps the collection bounded to
///     roughly one row per active user. Its absence is invisible until the
///     collection is a year old.
///   </item>
/// </list>
/// </summary>
public sealed class DigestIndexes : IMongoIndexProvider
{
    /// <summary>Mongoose's <c>expires: 60 * 60 * 24 * 7</c>.</summary>
    public const int TtlSeconds = 60 * 60 * 24 * 7;

    public string Name => "daily-digests";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var digests = database.GetCollection<BsonDocument>(DigestCollections.DailyDigests);

        await digests.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { ["userId"] = 1, ["localDate"] = 1 },
                    new CreateIndexOptions { Unique = true }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await digests.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { ["generatedAt"] = 1 },
                    new CreateIndexOptions { ExpireAfter = TimeSpan.FromSeconds(TtlSeconds) }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
