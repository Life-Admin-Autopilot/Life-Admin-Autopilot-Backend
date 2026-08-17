using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>One accepted candidate as the caller described it. Every override is optional.</summary>
public sealed record ReviewAccept(
    string Key,
    string? Title = null,
    string? Domain = null,
    string? Priority = null,
    DateTime? DueAt = null,
    string? Notes = null);

/// <summary>
/// Commits a review pass: accepted candidates become Tasks, discarded ones are
/// dropped, and the document keeps a record of which candidate became which Task.
///
/// <para>
/// <b>Not routed through <c>BulkService</c>, deliberately.</b> The kernel rule is
/// that multi-task MUTATIONS go through the journal so undo works. This is a
/// CREATE from a user's explicit accept, and Node writes it with a plain
/// <c>Task.bulkWrite</c> of upserts and no journal entry — adding one would put a
/// <c>TaskBulkOp</c> row on the .NET side that the reference server does not
/// have, and offer an undo the reference server does not offer.
/// </para>
/// </summary>
public interface IDocumentScanReviewService
{
    /// <summary>
    /// Idempotent per <c>(document, candidate key)</c>. The partial unique index on
    /// <c>{userId, sourceDocumentId, sourceTaskKey}</c> is what makes a
    /// double-tapped accept a no-op instead of a duplicate Task, which is why
    /// <see cref="DocumentScanIndexes"/> declares it.
    /// </summary>
    Task<IReadOnlyList<TaskDocument>> PersistAsync(
        ObjectId userId,
        ObjectId documentId,
        IReadOnlyList<ExtractedTaskCandidateDocument> items,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDocumentScanReviewService"/>
public sealed class DocumentScanReviewService : IDocumentScanReviewService
{
    private readonly IMongoCollection<TaskDocument> _tasks;
    private readonly IMongoCollection<BsonDocument> _rawTasks;

    public DocumentScanReviewService(IMongoDatabase database)
    {
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
        _rawTasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
    }

    public async Task<IReadOnlyList<TaskDocument>> PersistAsync(
        ObjectId userId,
        ObjectId documentId,
        IReadOnlyList<ExtractedTaskCandidateDocument> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return Array.Empty<TaskDocument>();
        }

        var now = DateTime.UtcNow;
        var writes = items
            .Select(item => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                RawIdentity(userId, documentId, item.Key),
                new BsonDocument("$setOnInsert", SeedTask(userId, documentId, item, now)))
            {
                IsUpsert = true,
            })
            .ToList();

        await _rawTasks
            .BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
            .ConfigureAwait(false);

        var keys = items.Select(i => i.Key).ToList();
        var found = await _tasks
            .Find(Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
                Builders<TaskDocument>.Filter.Eq(t => t.SourceDocumentId, documentId),
                Builders<TaskDocument>.Filter.In(t => t.SourceTaskKey, keys)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byKey = found
            .Where(t => t.SourceTaskKey is not null)
            .GroupBy(t => t.SourceTaskKey!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Returned in the order the caller accepted them, not in Mongo's order —
        // the response's `tasks` array lines up with the review card.
        return items
            .Select(i => byKey.TryGetValue(i.Key, out var task) ? task : null)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
    }

    private static FilterDefinition<BsonDocument> RawIdentity(ObjectId userId, ObjectId documentId, string key) =>
        Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", userId),
            Builders<BsonDocument>.Filter.Eq("sourceDocumentId", documentId),
            Builders<BsonDocument>.Filter.Eq("sourceTaskKey", key));

    /// <summary>
    /// The fields Node's <c>$setOnInsert</c> writes, PLUS the schema defaults
    /// Mongoose 8 adds for free (<c>setDefaultsOnInsert</c> is on by default). The
    /// optional three are conditional exactly as they are in Node: writing
    /// <c>dueAt: null</c> where Node omits the key would make the Task carry a due
    /// date that is present-and-empty rather than absent.
    /// </summary>
    private static BsonDocument SeedTask(
        ObjectId userId,
        ObjectId documentId,
        ExtractedTaskCandidateDocument item,
        DateTime now)
    {
        var seed = new BsonDocument
        {
            ["userId"] = userId,
            ["title"] = item.Title,
            ["domain"] = item.Domain,
            ["priority"] = item.Priority,
            ["status"] = "open",
            ["sourceDocumentId"] = documentId,
            ["sourceTaskKey"] = item.Key,
            ["confidence"] = item.Confidence,

            // Mongoose schema defaults, applied on upsert-insert.
            ["kind"] = "list",
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(),
            ["rescheduleCount"] = 0,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        if (item.Estimate is not null)
        {
            seed["estimate"] = new BsonDocument
            {
                ["minMinutes"] = item.Estimate.MinMinutes,
                ["maxMinutes"] = item.Estimate.MaxMinutes,
                ["source"] = item.Estimate.Source,
            };
        }

        // The figure follows the action onto the matter, which is what lets the
        // summary answer "what do I still owe" from Status/DueAt rather than
        // having to re-open the document it came from.
        if (item.Amount is not null)
        {
            seed["amount"] = new BsonDocument
            {
                ["amountMinor"] = item.Amount.AmountMinor,
                ["currency"] = item.Amount.Currency,
                ["source"] = item.Amount.Source,
                ["direction"] = item.Amount.Direction,
            };
        }

        if (item.DueAt is not null)
        {
            seed["dueAt"] = item.DueAt.Value;
        }

        if (!string.IsNullOrEmpty(item.Notes))
        {
            seed["notes"] = item.Notes;
        }

        return seed;
    }
}
