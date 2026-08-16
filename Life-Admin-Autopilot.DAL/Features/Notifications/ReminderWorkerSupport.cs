using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Notifications;

/// <summary>
/// The batched timezone lookup <c>reminderWorker.ts</c>'s <c>timezonesFor</c>
/// performs.
///
/// <para>
/// One query for the whole tick rather than a <c>User</c> lookup per task: a batch
/// can carry 100 tasks and they cluster onto far fewer accounts. The zone is
/// load-bearing rather than cosmetic — the notification NAMES a day, and a matter
/// due at <c>2026-03-05T01:00Z</c> is "Mar 5" in UTC but "Mar 4" in New York.
/// </para>
/// </summary>
public sealed class ReminderUserTimezoneReader : MongoRepositoryBase<UserProfileDocument>
{
    /// <summary>Node's <c>row.timezone ?? 'UTC'</c> — the field is optional by design.</summary>
    public const string DefaultTimezone = "UTC";

    public ReminderUserTimezoneReader(IMongoDatabase database)
        : base(database, MongoCollections.Users)
    {
    }

    public async Task<IReadOnlyDictionary<ObjectId, string>> ReadAsync(
        IReadOnlyCollection<ObjectId> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<ObjectId, string>();
        }

        var rows = await Collection
            .Find(Filter.In(u => u.Id, userIds))
            .Project(u => new { u.Id, u.Timezone })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byId = new Dictionary<ObjectId, string>(rows.Count);
        foreach (var row in rows)
        {
            byId[row.Id] = row.Timezone ?? DefaultTimezone;
        }

        return byId;
    }
}

/// <summary>
/// <c>settleStaleClarifications</c> — a question the user never answered SETTLES
/// on the AI's guess rather than nagging.
///
/// <para>
/// <b>This is a deliberate cross-slice write.</b> The clarifications collection
/// belongs to slice H, but in Node this update lives inside
/// <c>lib/reminderWorker.ts</c> and runs at the tail of every reminder tick. Moving
/// it would change when questions settle, so it is ported where Node put it and
/// documented here rather than relocated.
/// </para>
///
/// <para>
/// The window is a week, matching the Skip window, and deliberately generous:
/// settling is irreversible from the question's side and the user may simply have
/// been away. An earlier 6h value paced a recurring NAG, which is a different job
/// with very different stakes — an unresolved counter the user cannot clear turns
/// into guilt, and re-surfacing it on a timer is a shame mechanism, not a reminder.
/// </para>
/// </summary>
public sealed class StaleClarificationSettler : MongoRepositoryBase<ClarificationDocument>
{
    /// <summary><c>CLARIFICATION_SETTLE_MS</c> — seven days.</summary>
    public static readonly TimeSpan SettleAfter = TimeSpan.FromDays(7);

    /// <summary>The answer written onto a question that expired unanswered. Verbatim.</summary>
    public const string SettledAnswer = "Settled on the original guess.";

    public StaleClarificationSettler(IMongoDatabase database)
        : base(database, MongoCollections.Clarifications)
    {
    }

    /// <summary>
    /// <c>updateMany({status: 'open', createdAt: {$lte: cutoff}}, ...)</c>.
    ///
    /// <para>
    /// Note this is a bare <c>status: 'open'</c> test and NOT
    /// <see cref="MongoRepositoryBase{TDocument}.VisibleOpen"/>: a question the user
    /// deferred past the cutoff settles too. Composing <c>VisibleOpen</c> here — the
    /// reflex the kernel asks for on every LISTING surface — would leave deferred
    /// questions open forever. Ported as written.
    /// </para>
    /// </summary>
    public Task SettleStaleAsync(DateTime now, CancellationToken ct = default) =>
        Collection.UpdateManyAsync(
            Filter.And(
                Filter.Eq(c => c.Status, ClarificationVocabulary.Open),
                Filter.Lte(c => c.CreatedAt, now - SettleAfter)),
            Update
                .Set(c => c.Status, ClarificationVocabulary.Dropped)
                .Set(c => c.ResolvedAt, now)
                .Set(c => c.Answer, SettledAnswer),
            cancellationToken: ct);
}
