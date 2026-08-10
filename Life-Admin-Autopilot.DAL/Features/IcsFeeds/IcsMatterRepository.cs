using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.IcsFeeds;

/// <summary>
/// The Task-side reads and writes an ICS sync needs.
///
/// <para>
/// Deliberately NOT routed through <c>BulkService</c>. Node's ICS unsubscribe
/// calls <c>Task.updateMany</c> directly, so there is no <c>TaskBulkOp</c> journal
/// entry, no undo, and no clarification cascade for a retired feed matter. That is
/// inconsistent with every other task soft-delete in the product and it is what the
/// reference does — ported as-is rather than "fixed", because the response
/// (<c>retiredMatters</c>) and the absence of an undo entry are both observable.
/// </para>
/// </summary>
public sealed class IcsMatterRepository : MongoRepositoryBase<TaskDocument>
{
    /// <summary>Feed ids are hex ObjectIds. Asserted rather than assumed — see <see cref="ExternalIdPrefix"/>.</summary>
    private static readonly Regex HexObjectId = new("^[a-f0-9]{24}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IcsMatterRepository(IMongoDatabase database)
        : base(database, MongoCollections.Tasks)
    {
    }

    /// <summary><c>Task.externalId</c> for one occurrence of one subscription.</summary>
    public static string ExternalIdFor(string feedId, string occurrenceId) => $"{feedId}::{occurrenceId}";

    /// <summary>
    /// Anchored prefix matching every matter a subscription produced.
    ///
    /// <para>
    /// The hex assertion is the injection guard: today feed ids are ObjectIds and
    /// carry no regex metacharacters, but a future non-hex id would turn this into
    /// an injection point, so it fails loudly instead of quietly building a
    /// pattern out of user-reachable text.
    /// </para>
    /// </summary>
    public static BsonRegularExpression ExternalIdPrefix(string feedId)
    {
        if (!HexObjectId.IsMatch(feedId))
        {
            throw new InvalidOperationException($"Unexpected feed id shape: {feedId}");
        }

        return new BsonRegularExpression($"^{feedId}::");
    }

    /// <summary>
    /// Retire this subscription's FUTURE OPEN matters. "Stop this calendar" plainly
    /// means "stop these reminders".
    ///
    /// <para>
    /// Past and completed matters stay: they are a record of what happened, and
    /// deleting history is not what the user asked for. The sweep is scoped to THIS
    /// subscription by the anchored <c>externalId</c> prefix — an unscoped sweep
    /// would retire the matters of every other feed the user still subscribes to.
    /// </para>
    /// </summary>
    /// <returns>Mongo's <c>modifiedCount</c>, echoed to the client as <c>retiredMatters</c>.</returns>
    public async Task<long> RetireFutureOpenAsync(
        ObjectId userId,
        string feedId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection
            .UpdateManyAsync(
                Filter.And(
                    UserScoped(userId),
                    Filter.Eq(t => t.ExternalSource, IcsFeedVocabulary.ExternalSource),
                    Filter.Regex(t => t.ExternalId, ExternalIdPrefix(feedId)),
                    Filter.Eq(t => t.Status, "open"),
                    Filter.Gt(t => t.DueAt, now),
                    NotDeleted()),
                // `updatedAt` is set BY HAND. The reference is
                // `Task.updateMany(..., { $set: { deletedAt: now } })`
                // (routes/me.icsFeeds.ts:147) on a `timestamps: true` model, so
                // Mongoose adds `updatedAt` to that same `$set` itself and the Node
                // source never names the field. The .NET driver does not — see
                // KERNEL.md §7.
                Update
                    .Set(t => t.DeletedAt, now)
                    .Set(t => t.UpdatedAt, now),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.IsModifiedCountAvailable ? result.ModifiedCount : 0;
    }
}
