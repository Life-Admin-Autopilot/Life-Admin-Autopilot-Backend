using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Tasks;

/// <summary>
/// The Matters indexes from <c>models/Task.ts</c> that <c>KernelIndexProvider</c>
/// does not already create.
///
/// <para>
/// The kernel owns the load-bearing ones (the list's
/// <c>{userId, deletedAt, status, dueAt}</c>, the tag lookup, and the reminder
/// worker's claim scan). What is left are the secondary read paths this slice
/// adds: the Trash view's newest-deleted-first sort and the two narrower list
/// orderings. None carries a uniqueness invariant, so these are optimisations —
/// but they are declared in the Mongoose schema, and a collection that differs
/// between the two servers is a difference waiting to be debugged.
/// </para>
/// </summary>
public sealed class TaskIndexes : IMongoIndexProvider
{
    public string Name => "tasks";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var tasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);

        // Trash view — newest-deleted first.
        await PlainAsync(tasks, new BsonDocument { ["userId"] = 1, ["deletedAt"] = -1 }, cancellationToken)
            .ConfigureAwait(false);

        // Today screen: open tasks ordered by dueAt.
        await PlainAsync(
                tasks,
                new BsonDocument { ["userId"] = 1, ["status"] = 1, ["dueAt"] = 1 },
                cancellationToken)
            .ConfigureAwait(false);

        await PlainAsync(
                tasks,
                new BsonDocument { ["userId"] = 1, ["domain"] = 1, ["createdAt"] = -1 },
                cancellationToken)
            .ConfigureAwait(false);

        await PlainAsync(
                tasks,
                new BsonDocument { ["userId"] = 1, ["status"] = 1, ["priority"] = 1 },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task PlainAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument keys,
        CancellationToken cancellationToken) =>
        collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys),
            cancellationToken: cancellationToken);
}
