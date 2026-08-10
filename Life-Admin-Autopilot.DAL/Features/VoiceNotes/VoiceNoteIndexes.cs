using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.VoiceNotes;

/// <summary>
/// The indexes declared on <c>models/VoiceNote.ts</c>, plus the ONE uniqueness
/// invariant the extraction and review paths depend on.
///
/// <para>
/// The partial unique index on <c>tasks</c> over
/// <c>{userId, sourceVoiceNoteId, sourceTaskKey}</c> is NOT an optimisation: both
/// the worker and the review commit persist tasks as an upsert-per-item, and
/// without the index a worker reclaim (or a double-tapped accept) inserts a
/// second Task instead of being a no-op. It is partial so the many chat- and
/// scan-born tasks that carry no <c>sourceVoiceNoteId</c> do not all collide on
/// null.
/// </para>
///
/// <para>
/// The clarification side of that guarantee — the partial unique
/// <c>{userId, sourceKey}</c> on <c>clarifications</c> — is already created by
/// <c>KernelIndexProvider</c>, so it is deliberately absent here.
/// </para>
/// </summary>
public sealed class VoiceNoteIndexes : IMongoIndexProvider
{
    public string Name => "voice-notes";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var notes = database.GetCollection<BsonDocument>(MongoCollections.VoiceNotes);

        await CreateAsync(notes, new BsonDocument { ["userId"] = 1 }, null, cancellationToken).ConfigureAwait(false);

        await CreateAsync(notes, new BsonDocument { ["status"] = 1 }, null, cancellationToken).ConfigureAwait(false);

        await CreateAsync(notes, new BsonDocument { ["nextRunAt"] = 1 }, null, cancellationToken)
            .ConfigureAwait(false);

        // The list read: one user's notes, newest first.
        await CreateAsync(
                notes,
                new BsonDocument { ["userId"] = 1, ["createdAt"] = -1 },
                null,
                cancellationToken)
            .ConfigureAwait(false);

        // The worker's atomic claim query.
        await CreateAsync(
                notes,
                new BsonDocument { ["status"] = 1, ["nextRunAt"] = 1, ["lockedUntil"] = 1 },
                null,
                cancellationToken)
            .ConfigureAwait(false);

        // Extraction / review idempotency. UNIQUE is the invariant; PARTIAL keeps it
        // off the tasks that have no voice note behind them.
        await CreateAsync(
                database.GetCollection<BsonDocument>(MongoCollections.Tasks),
                new BsonDocument { ["userId"] = 1, ["sourceVoiceNoteId"] = 1, ["sourceTaskKey"] = 1 },
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    PartialFilterExpression = new BsonDocument
                    {
                        ["sourceTaskKey"] = new BsonDocument("$type", "string"),
                        ["sourceVoiceNoteId"] = new BsonDocument("$type", "objectId"),
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task CreateAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument keys,
        CreateIndexOptions<BsonDocument>? options,
        CancellationToken cancellationToken) =>
        collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys, options ?? new CreateIndexOptions<BsonDocument>()),
            cancellationToken: cancellationToken);
}
