using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Port of <c>server/src/modules/tasks/taskCounts.ts</c>.
///
/// <para>
/// This is the DASHBOARD's count source, and that is the whole point of it. Every
/// number on home used to come from <c>/me/digest</c>, which awaits a model call
/// to write its headline before it returns anything — so the counts were computed
/// in milliseconds and then held hostage by a sentence. Counts are deterministic
/// and cheap; prose is neither. They are served separately.
/// </para>
/// </summary>
public sealed class TaskCountsService
{
    private readonly IMongoDatabase _database;

    public TaskCountsService(IMongoDatabase database)
    {
        _database = database;
    }

    public async Task<TaskCountsDto> ComputeAsync(
        ObjectId userId,
        string? timezone,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        // An unrecognised zone THROWS here, exactly as Node's uncaught Intl
        // RangeError does. See TaskQuery.ZoneOffsetMinutes.
        var day = TaskQuery.GetDayBoundaries(now, timezone);

        var tasks = _database.GetCollection<TaskDocument>(MongoCollections.Tasks);

        var notDeleted = new BsonDocument("deletedAt", new BsonDocument("$exists", false));
        var scope = new BsonDocument { ["userId"] = userId, ["deletedAt"] = notDeleted["deletedAt"] };

        // "Live" is open-or-snoozed: the buckets describe work still in front of
        // the user, so something already done is in none of them.
        var live = new BsonDocument(scope) { ["status"] = new BsonDocument("$in", new BsonArray { "open", "snoozed" }) };

        BsonDocument Count(BsonDocument match) =>
            new("$match", match);

        var facet = new BsonDocument
        {
            ["overdue"] = Branch(Count(With(live, "dueAt", new BsonDocument("$lt", now)))),
            ["today"] = Branch(Count(With(live, "dueAt", Range(now, day.TomorrowStart)))),
            ["tomorrow"] = Branch(Count(With(live, "dueAt", Range(day.TomorrowStart, day.DayAfterTomorrowStart)))),
            ["thisWeek"] = Branch(Count(With(live, "dueAt", Range(day.DayAfterTomorrowStart, day.WeekEnd)))),
            ["later"] = Branch(Count(With(live, "dueAt", new BsonDocument("$gte", day.WeekEnd)))),
            ["undated"] = Branch(Count(With(live, "dueAt", new BsonDocument("$exists", false)))),
            ["open"] = Branch(Count(With(scope, "status", "open"))),
            ["done"] = Branch(Count(With(scope, "status", "done"))),

            // Slipping: pushed back repeatedly, or overdue by more than a
            // fortnight. Either signals a commitment that is not real any more.
            ["slipping"] = Branch(Count(With(
                live,
                "$or",
                new BsonArray
                {
                    new BsonDocument("rescheduleCount", new BsonDocument("$gte", 3)),
                    new BsonDocument("dueAt", new BsonDocument("$lt", day.TodayStart.AddDays(-14))),
                }))),

            ["completedToday"] = Branch(Count(new BsonDocument
            {
                ["userId"] = userId,
                ["status"] = "done",
                ["completedAt"] = Range(day.TodayStart, day.TomorrowStart),
            })),

            ["byDomain"] = Group(live, "$domain"),
            ["byPriority"] = Group(live, "$priority"),
        };

        var pipeline = new[]
        {
            new BsonDocument("$match", scope),
            new BsonDocument("$facet", facet),
        };

        var facetTask = tasks
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .FirstOrDefaultAsync(cancellationToken);

        // Three collections, so three reads — but concurrent, and all of them are
        // covered index counts. Trash lives OUTSIDE the main $match (which excludes
        // deleted rows), so it needs its own query too.
        var trashedTask = tasks.CountDocumentsAsync(
            Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
                Builders<TaskDocument>.Filter.Exists("deletedAt", true)),
            cancellationToken: cancellationToken);

        // visibleOpen() — NOT `status: 'open'`. A question the user SKIPPED is
        // deferred, not outstanding, and counting it would re-assert an obligation
        // they explicitly put down.
        var needsInputTask = _database
            .GetCollection<ClarificationDocument>(MongoCollections.Clarifications)
            .CountDocumentsAsync(
                Builders<ClarificationDocument>.Filter.And(
                    MongoRepositoryBase<ClarificationDocument>.UserScoped(userId),
                    MongoRepositoryBase<ClarificationDocument>.VisibleOpen(now)),
                cancellationToken: cancellationToken);

        // `ready_for_review` is the TERMINAL SUCCESS state of scanning, not a
        // to-do: every document that scanned cleanly keeps it forever. `reviewedAt`
        // is what the server stamps once every candidate is resolved, so its
        // ABSENCE is the real "needs you".
        var scansTask = _database
            .GetCollection<BsonDocument>(MongoCollections.ScannedDocuments)
            .CountDocumentsAsync(
                new BsonDocument
                {
                    ["userId"] = userId,
                    ["status"] = "ready_for_review",
                    ["reviewedAt"] = new BsonDocument("$exists", false),
                },
                cancellationToken: cancellationToken);

        await Task.WhenAll(facetTask, trashedTask, needsInputTask, scansTask).ConfigureAwait(false);

        var result = await facetTask.ConfigureAwait(false);

        return new TaskCountsDto
        {
            Overdue = Scalar(result, "overdue"),
            Today = Scalar(result, "today"),
            Tomorrow = Scalar(result, "tomorrow"),
            ThisWeek = Scalar(result, "thisWeek"),
            Later = Scalar(result, "later"),
            Undated = Scalar(result, "undated"),
            Open = Scalar(result, "open"),
            Done = Scalar(result, "done"),
            Trashed = (int)await trashedTask.ConfigureAwait(false),
            Slipping = Scalar(result, "slipping"),
            CompletedToday = Scalar(result, "completedToday"),
            NeedsInput = (int)await needsInputTask.ConfigureAwait(false),
            ScansAwaitingReview = (int)await scansTask.ConfigureAwait(false),
            ByDomain = Grouped(result, "byDomain"),
            ByPriority = Grouped(result, "byPriority"),
        };
    }

    private static BsonArray Branch(BsonDocument match) =>
        new() { match, new BsonDocument("$count", "n") };

    private static BsonArray Group(BsonDocument match, string field) =>
        new()
        {
            new BsonDocument("$match", match),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = field,
                ["n"] = new BsonDocument("$sum", 1),
            }),
        };

    private static BsonDocument Range(DateTime fromInclusive, DateTime toExclusive) =>
        new() { ["$gte"] = fromInclusive, ["$lt"] = toExclusive };

    /// <summary>A copy of <paramref name="baseMatch"/> with one extra clause.</summary>
    private static BsonDocument With(BsonDocument baseMatch, string field, BsonValue clause)
    {
        var copy = new BsonDocument(baseMatch);
        copy[field] = clause;
        return copy;
    }

    private static int Scalar(BsonDocument? facet, string key)
    {
        if (facet is null || !facet.TryGetValue(key, out var raw) || raw.AsBsonArray.Count == 0)
        {
            return 0;
        }

        return raw.AsBsonArray[0].AsBsonDocument.GetValue("n", 0).ToInt32();
    }

    private static IReadOnlyDictionary<string, int> Grouped(BsonDocument? facet, string key)
    {
        var output = new Dictionary<string, int>();
        if (facet is null || !facet.TryGetValue(key, out var raw))
        {
            return output;
        }

        foreach (var row in raw.AsBsonArray.Select(v => v.AsBsonDocument))
        {
            var id = row.GetValue("_id", BsonNull.Value);

            // Node skips falsy _id values — a row with no domain must not become
            // an empty-string bucket.
            if (id.IsBsonNull || id.AsString.Length == 0)
            {
                continue;
            }

            output[id.AsString] = row.GetValue("n", 0).ToInt32();
        }

        return output;
    }
}
