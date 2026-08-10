using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// The <c>aiconversations</c> indexes.
///
/// <para>
/// <c>KernelIndexProvider</c> already declares the unique
/// <c>{userId, date, kind}</c> index on <c>aiusagecounters</c> — the one the quota
/// primitive's duplicate-key retry depends on — so this slice adds only what the
/// conversation model declares that the kernel does not.
/// </para>
///
/// <para>
/// The <c>{userId, scope, scopeId}</c> index is <b>unique</b>, and that uniqueness
/// is load-bearing rather than cosmetic: <c>LoadAsync</c> and <c>ResetAsync</c> both
/// upsert on that triple, and without the constraint two concurrent first reads
/// would each insert a conversation and the user would silently end up with two
/// histories, one of which is invisible.
/// </para>
/// </summary>
public sealed class AiIndexes : IMongoIndexProvider
{
    public string Name => "ai";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var conversations = database.GetCollection<BsonDocument>(MongoCollections.AiConversations);

        await conversations.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { ["userId"] = 1, ["scope"] = 1, ["scopeId"] = 1 },
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Declared on the schema field itself (`userId: { index: true }`), so
        // Mongoose creates it too. Kept for lookup parity with the reference
        // database rather than for a query this slice issues.
        await conversations.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["userId"] = 1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
