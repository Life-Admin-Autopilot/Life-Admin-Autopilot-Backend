using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// The one read behind the agent's <c>MY TASKS</c> block. Derived from
/// <c>server/src/modules/ai/contextBuilder.ts</c>:
///
/// <code>
/// Task.find({ userId, ...notDeleted(), status: { $in: ['open', 'snoozed'] } })
///     .sort({ dueAt: 1, createdAt: -1 })
///     .limit(TASK_CAP)
/// </code>
///
/// <para>
/// <b>Read-only, and it owns nothing.</b> The Tasks slice owns the collection, its
/// writes, its eraser and its indexes; this is one projection of it for the prompt,
/// which is why it lives with the AI slice rather than growing a method on
/// <c>TaskRepository</c>. It composes <see cref="MongoRepositoryBase{TDocument}.LiveForUser"/>
/// like every other Task read, because a trashed matter reaching the prompt is worse
/// than no prompt at all: the agent cites it back and the user rightly concludes the
/// delete failed.
/// </para>
///
/// <para>
/// <b>THE SORT DIVERGES FROM THE REFERENCE ON PURPOSE. Recorded in
/// <c>docs/DIVERGENCES.md</c>.</b> Ascending <c>dueAt</c> puts missing-field rows FIRST
/// in Mongo's BSON ordering, so the reference's order is "undated backlog, then the
/// soonest deadlines" — and the cap then truncates from the far end. Measured on the
/// seeded demo account (143 open matters): the twenty rows the agent received were 14
/// undated plus 6 dated, and all six dated rows were in the PAST — spanning
/// 2026-06-09 to 2026-08-05 against a clock reading 2026-08-20. Not one upcoming matter
/// could reach the prompt, at any cap, for any account with twenty-odd undated items.
/// An agent asked "what do I have on Friday" was structurally incapable of seeing it.
/// </para>
///
/// <para>
/// The fix is the sentinel the rest of the server already uses:
/// <c>TaskQuery.FarFuture</c> substituted for a missing <c>dueAt</c>, exactly as
/// <c>TaskQuery.ListAsync</c> does for the REST list and the UI. Undated matters now
/// sort LAST, which means the window leads with the work that has a date on it and the
/// agent and the matter list finally agree about what "first" means.
/// </para>
///
/// <para>
/// <b>Why a plain sort cannot express that.</b> Mongo orders missing fields before
/// every value; there is no sort direction that puts them last. The substitution has to
/// happen in the query, so this is an aggregation rather than a <c>Find</c> — the same
/// <c>$ifNull</c> + <c>$sort</c> + <c>$limit</c> shape <c>TaskQuery.ListAsync</c> builds.
/// </para>
/// </summary>
public sealed class AiGroundingRepository : MongoRepositoryBase<TaskDocument>
{
    /// <summary>
    /// Substituted for a missing <c>dueAt</c> so undated matters sort last. The same
    /// instant <c>TaskQuery.FarFuture</c> uses; duplicated rather than referenced
    /// because the DAL does not depend on the BLL, and a mismatch here would only
    /// reorder rows rather than break anything loudly — so the value is pinned by
    /// <c>AiGroundingRepositoryTests</c> instead.
    /// </summary>
    public static readonly DateTime UndatedSortsLast = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    public AiGroundingRepository(IMongoDatabase database)
        : base(database, MongoCollections.Tasks)
    {
    }

    public async Task<IReadOnlyList<TaskDocument>> ListForPromptAsync(
        ObjectId userId,
        IReadOnlyList<string> statuses,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var match = Builders<TaskDocument>.Filter
            .And(LiveForUser(userId), Filter.In(task => task.Status, statuses))
            .Render(new RenderArgs<TaskDocument>(
                Collection.DocumentSerializer,
                Collection.Settings.SerializerRegistry));

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$addFields", new BsonDocument(
                "_dueSort",
                new BsonDocument("$ifNull", new BsonArray { "$dueAt", UndatedSortsLast }))),
            new BsonDocument("$sort", new BsonDocument
            {
                ["_dueSort"] = 1,
                ["createdAt"] = -1,
            }),
            new BsonDocument("$limit", limit),
            new BsonDocument("$unset", "_dueSort"),
        };

        return await Collection
            .Aggregate<TaskDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
