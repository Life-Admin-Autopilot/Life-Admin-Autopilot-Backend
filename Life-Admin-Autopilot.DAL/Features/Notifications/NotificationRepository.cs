using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Notifications;

/// <summary>
/// Every read and write the notifications slice performs against the
/// <c>notifications</c> collection — ports <c>routes/me.notifications.ts</c> plus
/// the row the reminder worker writes.
///
/// <para>
/// Notifications are NOT soft-deletable, so <c>NotDeleted()</c> does not apply
/// here; the collection has no <c>deletedAt</c> field at all. Every read is still
/// scoped with <see cref="MongoRepositoryBase{TDocument}.UserScoped"/>.
/// </para>
/// </summary>
public sealed class NotificationRepository : MongoRepositoryBase<NotificationDocument>
{
    /// <summary>Node's <c>.limit(50)</c> on the feed. No pagination — there is no cursor.</summary>
    public const int FeedLimit = 50;

    public NotificationRepository(IMongoDatabase database)
        : base(database, MongoCollections.Notifications)
    {
    }

    /// <summary>
    /// <c>Notification.find({userId}).sort({createdAt: -1}).limit(50)</c>.
    /// </summary>
    public Task<List<NotificationDocument>> FindFeedAsync(ObjectId userId, CancellationToken ct = default) =>
        Collection
            .Find(UserScoped(userId))
            .Sort(Sort.Descending(n => n.CreatedAt))
            .Limit(FeedLimit)
            .ToListAsync(ct);

    /// <summary>
    /// <c>countDocuments({userId, readAt: null})</c>.
    ///
    /// <para>
    /// <b><c>readAt: null</c>, not <c>$exists: false</c>.</b> Mongo's null-equality
    /// matches a document where the field is ABSENT as well as one where it is
    /// explicitly null, and that is exactly what makes the count work: the worker
    /// writes rows with no <c>readAt</c> at all. Rewriting this as an
    /// <c>$exists</c> test — the reflex borrowed from <c>NotDeleted()</c> — would
    /// silently stop counting every row a client has never touched.
    /// </para>
    /// </summary>
    public Task<long> CountUnreadAsync(ObjectId userId, CancellationToken ct = default) =>
        Collection.CountDocumentsAsync(Unread(userId), cancellationToken: ct);

    /// <summary>
    /// <c>updateMany({userId, readAt: null, [_id: {$in: ids}]}, {$set: {readAt: now}})</c>.
    ///
    /// <para>
    /// A null or EMPTY <paramref name="ids"/> marks every unread row read — that is
    /// Node's <c>if (ids &amp;&amp; ids.length > 0)</c>, where an empty array is
    /// falsy for this purpose and therefore never narrows the filter. A non-empty
    /// list that filtered down to nothing (all entries were malformed ObjectIds)
    /// still narrows, producing an empty <c>$in</c> and a deliberate no-op.
    /// </para>
    ///
    /// <para>
    /// <b><c>updatedAt</c> is bumped even though the Node route never mentions
    /// it.</b> Mongoose adds it to the <c>$set</c> itself for any model declared
    /// with <c>timestamps: true</c>, and the driver does not — so a literal port of
    /// the route body leaves the field stale and the very next
    /// <c>GET /me/notifications</c> diverges. Caught by a seeded differential
    /// against <c>:4200</c>, where the reference answers
    /// <c>updatedAt == readAt</c> to the millisecond; the same <c>now</c> is used
    /// for both here for that reason.
    /// </para>
    /// </summary>
    public Task MarkReadAsync(
        ObjectId userId,
        IReadOnlyList<ObjectId>? ids,
        DateTime now,
        CancellationToken ct = default)
    {
        var filter = ids is null
            ? Unread(userId)
            : Filter.And(Unread(userId), Filter.In(n => n.Id, ids));

        return Collection.UpdateManyAsync(
            filter,
            Update.Set(n => n.ReadAt, now).Set(n => n.UpdatedAt, now),
            cancellationToken: ct);
    }

    /// <summary>The reminder worker's <c>Notification.create(...)</c>.</summary>
    public Task InsertAsync(NotificationDocument notification, CancellationToken ct = default) =>
        Collection.InsertOneAsync(notification, cancellationToken: ct);

    private static FilterDefinition<NotificationDocument> Unread(ObjectId userId) =>
        Filter.And(UserScoped(userId), Filter.Eq(n => n.ReadAt, null));
}
