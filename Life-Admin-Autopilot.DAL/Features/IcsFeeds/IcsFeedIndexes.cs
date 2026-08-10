using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.IcsFeeds;

/// <summary>
/// The indexes <c>models/IcsFeed.ts</c> declares. Mongoose creates them from the
/// schema; the .NET driver creates nothing, so they are declared here.
///
/// <para>
/// The <c>(userId, url)</c> one carries a real invariant rather than being an
/// optimisation: without it, two concurrent subscribes to the same URL both miss
/// the existence probe and insert, and every event that feed produces is fanned
/// out twice under two feed ids.
/// </para>
/// </summary>
public sealed class IcsFeedIndexes : IMongoIndexProvider
{
    public string Name => "ics-feeds";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var feeds = database.GetCollection<BsonDocument>(MongoCollections.IcsFeeds);

        await CreateAsync(
                feeds,
                new BsonDocument { ["userId"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        // One subscription per (user, URL). See the class remark.
        await CreateAsync(
                feeds,
                new BsonDocument { ["userId"] = 1, ["url"] = 1 },
                new CreateIndexOptions<BsonDocument> { Unique = true },
                cancellationToken)
            .ConfigureAwait(false);

        await CreateAsync(
                feeds,
                new BsonDocument { ["status"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        // Poller claim: oldest-fetched active feeds first.
        await CreateAsync(
                feeds,
                new BsonDocument { ["status"] = 1, ["lastFetchedAt"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        // The reconcile probe and the unsubscribe sweep both key on this triple, so
        // the index is needed for speed — a collection scan per event is what makes a
        // 400-occurrence series slow.
        //
        // But it MUST be unique and partial, matching `models/Task.ts:427`. An earlier
        // comment here claimed it was "not declared in Mongoose"; it is, and the
        // reference's own note says why: without the constraint a re-sync can split a
        // single reminder out into duplicates. A plain index of the same shape is
        // worse than none, because it satisfies the "does an index exist" check while
        // silently dropping the guarantee that makes imports idempotent.
        //
        // Partial so only IMPORTED rows are constrained — manual, voice and document
        // matters carry no externalId, and a unique index over missing fields would
        // collapse them all into one.
        await CreateAsync(
                database.GetCollection<BsonDocument>(MongoCollections.Tasks),
                new BsonDocument { ["userId"] = 1, ["externalSource"] = 1, ["externalId"] = 1 },
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    PartialFilterExpression = new BsonDocument
                    {
                        ["externalSource"] = new BsonDocument("$type", "string"),
                        ["externalId"] = new BsonDocument("$type", "string"),
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task CreateAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument keys,
        CreateIndexOptions<BsonDocument> options,
        CancellationToken cancellationToken) =>
        collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocumentIndexKeysDefinition<BsonDocument>(keys), options),
            cancellationToken: cancellationToken);
}

/// <summary>
/// A deleted account's subscriptions. Registered at
/// <see cref="Kernel.UserData.UserErasureOrder.Dependents"/> — a feed holds no blob
/// storage, so it needs no <c>Storage</c>-order pass.
/// </summary>
public sealed class IcsFeedEraser : Kernel.UserData.MongoCollectionEraser
{
    public IcsFeedEraser(IMongoDatabase database)
        : base("ics-feeds", MongoCollections.IcsFeeds) => UseDatabase(database);
}
