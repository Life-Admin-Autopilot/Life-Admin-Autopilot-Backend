using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Kernel.Tasks;

/// <summary>
/// Port of <c>server/src/modules/clarifications/dropOpenClarifications.ts</c>.
///
/// <para>
/// Lives in the kernel rather than the clarifications slice because
/// <see cref="BulkService"/> — the only journaled delete path — depends on it.
/// A question is ABOUT a task; once the task is gone there is nothing left to
/// clarify, and leaving it open strands a prompt the user can never meaningfully
/// answer. That is exactly what made "Questions for you" outlive everything the
/// user had cleared.
/// </para>
/// </summary>
public sealed class ClarificationCascade
{
    private readonly IMongoCollection<ClarificationDocument> _clarifications;

    public ClarificationCascade(IMongoDatabase database)
    {
        _clarifications = database.GetCollection<ClarificationDocument>(MongoCollections.Clarifications);
    }

    /// <summary>Per-task cascade — the delete path for one or many specific tasks.</summary>
    public async Task<long> DropForTasksAsync(
        ObjectId userId,
        IReadOnlyList<ObjectId> taskIds,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        var result = await _clarifications
            .UpdateManyAsync(
                Builders<ClarificationDocument>.Filter.And(
                    Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
                    Builders<ClarificationDocument>.Filter.Eq(c => c.Status, "open"),
                    Builders<ClarificationDocument>.Filter.In(c => c.TaskId, taskIds)),
                Builders<ClarificationDocument>.Update
                    .Set(c => c.Status, "dropped")
                    .Set(c => c.ResolvedAt, DateTime.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }

    /// <summary>
    /// Blanket drop for a "clear everything" wipe, optionally scoped by
    /// <c>draft.domain</c>.
    ///
    /// <para>A STATUS-scoped wipe deliberately drops nothing: status describes a
    /// task's lifecycle, and the per-task cascade above already covers whatever
    /// that wipe actually deleted. Pass no domain only for a true blanket wipe.</para>
    /// </summary>
    public async Task<long> DropOpenAsync(
        ObjectId userId,
        string? domain = null,
        CancellationToken cancellationToken = default)
    {
        var clauses = new List<FilterDefinition<ClarificationDocument>>
        {
            Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<ClarificationDocument>.Filter.Eq(c => c.Status, "open"),
        };

        if (!string.IsNullOrEmpty(domain))
        {
            clauses.Add(Builders<ClarificationDocument>.Filter.Eq("draft.domain", domain));
        }

        var result = await _clarifications
            .UpdateManyAsync(
                Builders<ClarificationDocument>.Filter.And(clauses),
                Builders<ClarificationDocument>.Update
                    .Set(c => c.Status, "dropped")
                    .Set(c => c.ResolvedAt, DateTime.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }
}
