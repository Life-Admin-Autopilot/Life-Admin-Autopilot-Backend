using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.DocumentScans;

/// <summary>
/// The indexes declared on <c>models/ScannedDocument.ts</c>, plus the ONE
/// uniqueness invariant the review path depends on.
///
/// <para>
/// <c>KernelIndexProvider</c> already creates the unique
/// <c>{userId, month}</c> index on <c>documentscanusagecounters</c>, so it is
/// deliberately absent here — that one is load-bearing for the quota primitive's
/// duplicate-key retry and the kernel owns it.
/// </para>
///
/// <para>
/// What this slice must add is the PARTIAL UNIQUE index on <c>tasks</c> over
/// <c>{userId, sourceDocumentId, sourceTaskKey}</c>. It is not an optimisation:
/// the review commit is an upsert-per-candidate, and without the index a
/// double-tapped "accept" inserts a second Task instead of being a no-op. It is
/// partial so the many chat- and voice-born tasks that carry no
/// <c>sourceDocumentId</c> do not all collide on null.
/// </para>
/// </summary>
public sealed class DocumentScanIndexes : IMongoIndexProvider
{
    public string Name => "document-scans";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var scans = database.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments);

        await CreateAsync(
                scans,
                new BsonDocument { ["userId"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        // The list read: one user's scans, newest first.
        await CreateAsync(
                scans,
                new BsonDocument { ["userId"] = 1, ["createdAt"] = -1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        await CreateAsync(
                scans,
                new BsonDocument { ["status"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        await CreateAsync(
                scans,
                new BsonDocument { ["nextRunAt"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        // The worker's atomic claim scan.
        await CreateAsync(
                scans,
                new BsonDocument { ["status"] = 1, ["nextRunAt"] = 1, ["lockedUntil"] = 1 },
                new CreateIndexOptions<BsonDocument>(),
                cancellationToken)
            .ConfigureAwait(false);

        // Review-commit idempotency. UNIQUE is the invariant; PARTIAL keeps it off
        // the tasks that have no document behind them.
        await CreateAsync(
                database.GetCollection<BsonDocument>(MongoCollections.Tasks),
                new BsonDocument { ["userId"] = 1, ["sourceDocumentId"] = 1, ["sourceTaskKey"] = 1 },
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    PartialFilterExpression = new BsonDocument
                    {
                        ["sourceTaskKey"] = new BsonDocument("$type", "string"),
                        ["sourceDocumentId"] = new BsonDocument("$type", "objectId"),
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
            new CreateIndexModel<BsonDocument>(keys, options),
            cancellationToken: cancellationToken);
}
