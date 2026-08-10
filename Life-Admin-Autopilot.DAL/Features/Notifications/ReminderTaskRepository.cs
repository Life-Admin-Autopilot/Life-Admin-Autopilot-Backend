using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Notifications;

/// <summary>
/// The three reminder-shaped queries over the <c>tasks</c> collection:
/// the device's upcoming schedule, the worker's due batch, and the per-entry
/// atomic claim.
///
/// <para>
/// <b>Why this is not a method on the Matters slice's <c>TaskRepository</c>.</b>
/// These reads belong to <c>me.reminders.ts</c> and <c>lib/reminderWorker.ts</c>,
/// which are this slice's Node sources, and adding them to another slice's file
/// would put two agents in the same class. It is a read-and-claim projection over
/// a collection the Matters slice owns, so it composes
/// <see cref="MongoRepositoryBase{TDocument}.NotDeleted"/> exactly as every other
/// Task read must (KERNEL.md §7) and touches nothing else on the document.
/// </para>
/// </summary>
public sealed class ReminderTaskRepository : MongoRepositoryBase<TaskDocument>
{
    /// <summary>
    /// <c>HORIZON_DAYS</c>. Deliberately generous: iOS can only schedule local
    /// notifications while the app is open, so the window has to cover a plausible
    /// gap between visits.
    /// </summary>
    public const int HorizonDays = 30;

    /// <summary>
    /// <c>MAX_REMINDERS</c>. iOS caps an app at 64 pending local notifications and
    /// silently drops the rest, so we send the soonest ones and let the next sync
    /// top up.
    /// </summary>
    public const int MaxReminders = 60;

    /// <summary><c>BATCH</c> — the worker's per-tick task ceiling.</summary>
    public const int WorkerBatch = 100;

    /// <summary>Statuses a reminder can still fire for. A done matter is silent.</summary>
    private static readonly string[] LiveStatuses = { "open", "snoozed" };

    public ReminderTaskRepository(IMongoDatabase database)
        : base(database, MongoCollections.Tasks)
    {
    }

    /// <summary>
    /// <c>GET /me/reminders/upcoming</c>'s task query: live, not done, holding at
    /// least one un-fired reminder inside the horizon. Sorted <c>{dueAt: 1}</c> and
    /// capped at <see cref="MaxReminders"/> TASKS — the flattened list is capped
    /// again afterwards.
    /// </summary>
    public Task<List<TaskDocument>> FindWithUpcomingRemindersAsync(
        ObjectId userId,
        DateTime now,
        DateTime horizon,
        CancellationToken ct = default) =>
        Collection
            .Find(Filter.And(
                LiveForUser(userId),
                Filter.In(t => t.Status, LiveStatuses),
                UnfiredBetween(after: now, throughInclusive: horizon)))
            .Sort(Sort.Ascending(t => t.DueAt))
            .Limit(MaxReminders)
            .ToListAsync(ct);

    /// <summary>
    /// The worker's candidate batch. Deliberately NOT user-scoped — the worker
    /// sweeps every account.
    ///
    /// <para>
    /// The soft-delete guard matters here more than anywhere: firing a notification
    /// for a matter the user just deleted is the worst possible way to learn the
    /// delete did not stick.
    /// </para>
    /// </summary>
    public Task<List<TaskDocument>> FindDueBatchAsync(DateTime now, CancellationToken ct = default) =>
        Collection
            .Find(Filter.And(
                NotDeleted(),
                Filter.In(t => t.Status, LiveStatuses),
                UnfiredThrough(now)))
            .Limit(WorkerBatch)
            .ToListAsync(ct);

    /// <summary>
    /// Atomically claim ONE reminder entry: match it while <c>firedAt</c> is still
    /// null and stamp it in the same round trip.
    ///
    /// <para>
    /// <b>This — not the scheduler — is the double-send guard.</b> The positional
    /// <c>reminders.$</c> updates exactly the entry the <c>$elemMatch</c> selected,
    /// so a second tick racing the first finds no match and returns null. Splitting
    /// this into a read-then-write, or claiming the whole task at once, reintroduces
    /// the double-send this shape exists to prevent.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when this caller won the claim.</returns>
    public async Task<bool> TryClaimReminderAsync(
        ObjectId taskId,
        DateTime at,
        DateTime firedAt,
        CancellationToken ct = default)
    {
        var claimed = await Collection
            .FindOneAndUpdateAsync(
                Filter.And(
                    Filter.Eq(t => t.Id, taskId),
                    Filter.ElemMatch(
                        t => t.Reminders,
                        Builders<ReminderEntryDocument>.Filter.And(
                            Builders<ReminderEntryDocument>.Filter.Eq(r => r.At, at),
                            Builders<ReminderEntryDocument>.Filter.Eq(r => r.FiredAt, null)))),
                Update.Set("reminders.$.firedAt", firedAt),
                cancellationToken: ct)
            .ConfigureAwait(false);

        // Node's findOneAndUpdate defaults to returning the PRE-image, so a null
        // here means "no document matched" — i.e. somebody else already claimed it.
        return claimed is not null;
    }

    /// <summary>
    /// <c>reminders: {$elemMatch: {firedAt: null, at: {$lte: now}}}</c>.
    ///
    /// <para><c>firedAt: null</c> matches an entry where the field is absent, which
    /// is how every freshly planned reminder is stored.</para>
    /// </summary>
    private static FilterDefinition<TaskDocument> UnfiredThrough(DateTime now) =>
        Filter.ElemMatch(
            t => t.Reminders,
            Builders<ReminderEntryDocument>.Filter.And(
                Builders<ReminderEntryDocument>.Filter.Eq(r => r.FiredAt, null),
                Builders<ReminderEntryDocument>.Filter.Lte(r => r.At, now)));

    /// <summary><c>{firedAt: null, at: {$gt: now, $lte: horizon}}</c> — note $gt, not $gte.</summary>
    private static FilterDefinition<TaskDocument> UnfiredBetween(DateTime after, DateTime throughInclusive) =>
        Filter.ElemMatch(
            t => t.Reminders,
            Builders<ReminderEntryDocument>.Filter.And(
                Builders<ReminderEntryDocument>.Filter.Eq(r => r.FiredAt, null),
                Builders<ReminderEntryDocument>.Filter.Gt(r => r.At, after),
                Builders<ReminderEntryDocument>.Filter.Lte(r => r.At, throughInclusive)));
}
