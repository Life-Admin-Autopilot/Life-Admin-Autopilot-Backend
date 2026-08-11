using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// The one read behind the agent's <c>MY TASKS</c> block —
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
/// <b>The sort is not cosmetic.</b> Ascending <c>dueAt</c> puts the missing-field rows
/// FIRST in Mongo's BSON ordering, so an undated backlog leads and the soonest
/// deadlines follow. That is the order the reference produces and the order the cap
/// then truncates, so changing it changes WHICH twenty matters the agent sees.
/// </para>
/// </summary>
public sealed class AiGroundingRepository : MongoRepositoryBase<TaskDocument>
{
    public AiGroundingRepository(IMongoDatabase database)
        : base(database, MongoCollections.Tasks)
    {
    }

    public async Task<IReadOnlyList<TaskDocument>> ListForPromptAsync(
        ObjectId userId,
        IReadOnlyList<string> statuses,
        int limit,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.And(LiveForUser(userId), Filter.In(task => task.Status, statuses)))
            .Sort(Sort.Ascending(task => task.DueAt).Descending(task => task.CreatedAt))
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
