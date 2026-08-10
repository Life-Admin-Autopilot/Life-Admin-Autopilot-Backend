using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Driver;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Kernel.Integrations;

/// <param name="ExternalId">Stable per-item id from the source. Must not change between polls.</param>
/// <param name="Kind">
/// <c>reminder</c> fires; <c>list</c> is passive. An item whose time we had to assume
/// must arrive as <c>list</c> — ask rather than act on a guess, and a passive matter
/// is how "ask" looks before the user answers.
/// </param>
/// <param name="Completed">Source says this is finished. Completed items are not resurrected as open.</param>
/// <param name="SourceHasOwnAlerts">
/// The source will already alert the user at the deadline itself — a calendar event
/// carrying its own <c>reminders.overrides</c>, for instance. When set, Kitto drops
/// its own at-due nudge and keeps only the lead-time one, because notification
/// pile-up is the exact problem the reminder-conflict spec exists to solve. The
/// lead-time nudge is genuinely additive: Google says "in 10 minutes", Kitto says
/// "tomorrow, bring the referral letter".
/// </param>
public sealed record ExternalMatterInput(
    ObjectId UserId,
    string ExternalSource,
    string ExternalId,
    string Title,
    string Domain,
    DateTime DueAt,
    string Kind,
    string TimePrecision,
    string Confidence,
    string? Notes = null,
    bool Completed = false,
    bool SourceHasOwnAlerts = false);

/// <param name="Skipped">Set when nothing happened and why, so callers can report honestly.</param>
public readonly record struct ReconcileOutcome(bool Created, bool Updated, string? Skipped = null);

/// <summary>
/// The three rules for turning an external item into a matter. Port of
/// <c>server/src/modules/integrations/reconcileMatter.ts</c>.
///
/// <list type="number">
///   <item>
///     <b>Upsert on <c>(userId, externalSource, externalId)</c>.</b> Nothing we sync
///     from has reliable push, so every importer re-reads an overlapping window. A
///     blind insert is how one appointment becomes a matter every hour.
///   </item>
///   <item>
///     <b>A matter the user deleted STAYS deleted.</b> Task soft-deletes, so an
///     importer that ignored <c>deletedAt</c> would resurrect everything the user
///     swept away — and they would have to sweep it again after every poll.
///   </item>
///   <item>
///     <b>Updates touch TIMING only.</b> Once a matter exists the user owns its
///     title, notes and domain; rewriting them would silently undo "rename this to
///     something I understand". The source stays authoritative for WHEN, the user
///     for WHAT.
///   </item>
/// </list>
///
/// <para>
/// <b>Shared by both importers — this is the only copy.</b> The ICS and Google
/// slices were built in parallel and each shipped its own, because neither branch
/// could consume the other's uncommitted work. They were consolidated here on the
/// merge: a divergence between two copies of these rules is a user-visible bug, not
/// a style issue, and shared integration logic with two consumers is what
/// <c>Kernel/</c> is for.
/// </para>
///
/// <para>
/// <b>On the reference's ICS path.</b> In Node this module is imported only by the
/// two Google importers — <c>ics/syncFeed.ts</c> does NOT call it, it inlines the
/// same three rules (verified: <c>grep reconcileMatter</c> across
/// <c>server/src</c> matches only <c>google/syncGoogleCalendar.ts</c> and
/// <c>google/syncGoogleTasks.ts</c>). The inlined copy is behaviourally identical to
/// driving this one with <c>Completed</c> and <c>SourceHasOwnAlerts</c> both false:
/// its guard <c>!(movedBy &gt; 60_000 || existing.kind !== kind)</c> is exactly
/// <see cref="SameMoment"/> plus the kind check, and the two branches those flags
/// gate are unreachable. So the ICS sync drives this shared copy and stays faithful.
/// </para>
/// </summary>
public sealed class ExternalMatterReconciler
{
    /// <summary>
    /// Under a minute apart is the same moment — sources round differently, and a
    /// re-plan on every poll would clear <c>firedAt</c> and re-fire nudges the user
    /// has already had.
    /// </summary>
    private static readonly TimeSpan SameMoment = TimeSpan.FromMilliseconds(60_000);

    private readonly IMongoCollection<TaskDocument> _tasks;
    private readonly ReminderPlanner _reminders;

    public ExternalMatterReconciler(IMongoDatabase database, ReminderPlanner reminders)
    {
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
        _reminders = reminders;
    }

    public async Task<ReconcileOutcome> ReconcileAsync(
        ExternalMatterInput input,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        // Rule 1. All THREE parts of the key — a reconciler shared by two sources
        // must not assume its own.
        var filter = Builders<TaskDocument>.Filter.And(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, input.UserId),
            Builders<TaskDocument>.Filter.Eq(t => t.ExternalSource, input.ExternalSource),
            Builders<TaskDocument>.Filter.Eq(t => t.ExternalId, input.ExternalId));

        var existing = await _tasks.Find(filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Rule 2. The user threw this away; leave it thrown.
        if (existing?.DeletedAt is not null)
        {
            return new ReconcileOutcome(false, false, "user_deleted");
        }

        if (existing is null)
        {
            // Nothing to create for something that arrived already finished —
            // importing a year of completed items as fresh matters would bury the
            // real list.
            if (input.Completed)
            {
                return new ReconcileOutcome(false, false);
            }

            var task = new TaskDocument
            {
                Id = ObjectId.GenerateNewId(now),
                UserId = input.UserId,
                Title = input.Title,
                Domain = input.Domain,
                Kind = input.Kind,
                Status = "open",
                Priority = "normal",
                Subtasks = new List<SubtaskDocument>(),
                Tags = new List<string>(),
                DueAt = input.DueAt,
                Notes = input.Notes,
                ExternalSource = input.ExternalSource,
                ExternalId = input.ExternalId,
                TimePrecision = input.TimePrecision,
                Confidence = input.Confidence,
                Reminders = new List<ReminderEntryDocument>(),
                RescheduleCount = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _tasks.InsertOneAsync(task, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _reminders.SetRulesRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
            await DropDuplicateDueNudgeAsync(task, input, now, cancellationToken).ConfigureAwait(false);
            return new ReconcileOutcome(true, false);
        }

        // Completion propagates in ONE direction only: the source can close a matter,
        // never reopen one. A user who ticked something off in Kitto must not have it
        // un-ticked by a stale row upstream.
        if (input.Completed && existing.Status != "done")
        {
            existing.Status = "done";
            existing.CompletedAt = now;
            existing.UpdatedAt = now;

            await _tasks
                .UpdateOneAsync(
                    Builders<TaskDocument>.Filter.Eq(t => t.Id, existing.Id),
                    Builders<TaskDocument>.Update
                        .Set(t => t.Status, existing.Status)
                        .Set(t => t.CompletedAt, existing.CompletedAt)
                        .Set(t => t.UpdatedAt, now),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new ReconcileOutcome(false, true);
        }

        // Rule 3.
        var movedBy = existing.DueAt.HasValue
            ? (existing.DueAt.Value - input.DueAt).Duration()
            : TimeSpan.MaxValue;

        if (movedBy <= SameMoment && existing.Kind == input.Kind)
        {
            return new ReconcileOutcome(false, false);
        }

        existing.DueAt = input.DueAt;
        existing.Kind = input.Kind;
        existing.TimePrecision = input.TimePrecision;
        existing.Confidence = input.Confidence;
        existing.UpdatedAt = now;

        await _tasks
            .UpdateOneAsync(
                Builders<TaskDocument>.Filter.Eq(t => t.Id, existing.Id),
                Builders<TaskDocument>.Update
                    .Set(t => t.DueAt, existing.DueAt)
                    .Set(t => t.Kind, existing.Kind)
                    .Set(t => t.TimePrecision, existing.TimePrecision)
                    .Set(t => t.Confidence, existing.Confidence)
                    .Set(t => t.UpdatedAt, now),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Re-plan ONLY because the deadline actually moved — the planner clears
        // firedAt, so calling it on an unchanged task would re-fire nudges the user
        // already received.
        await _reminders.SetRulesRemindersAsync(existing, now, cancellationToken).ConfigureAwait(false);
        await DropDuplicateDueNudgeAsync(existing, input, now, cancellationToken).ConfigureAwait(false);
        return new ReconcileOutcome(false, true);
    }

    /// <summary>
    /// <c>SetRulesRemindersAsync</c> always writes an at-due entry, which is right for
    /// a matter Kitto owns and wrong for one the source already alerts on. Stripping
    /// it afterwards keeps the lead-time logic in exactly one place rather than
    /// teaching the planner about integrations.
    /// </summary>
    private async Task DropDuplicateDueNudgeAsync(
        TaskDocument task,
        ExternalMatterInput input,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!input.SourceHasOwnAlerts)
        {
            return;
        }

        var kept = task.Reminders.Where(entry => entry.Kind != "due").ToList();
        if (kept.Count == task.Reminders.Count)
        {
            return;
        }

        task.Reminders = kept;
        task.UpdatedAt = now;

        // `updatedAt` set BY HAND, for the reason in KERNEL.md §7.0: the reference
        // reaches this through `task.save()` on a `timestamps: true` model, so
        // Mongoose stamps it and the Node source never names the field.
        //
        // Honest note: here it is DEFENSIVE, not a fix for an observable bug. This
        // helper only ever runs immediately after the insert or the timing update
        // above, both of which already set `UpdatedAt` to this same `now` — so
        // omitting it, as both slice copies did, changed nothing you could read
        // back. It is written anyway so the method is correct in isolation, because
        // the next caller added to it will not be preceded by a write.
        await _tasks
            .UpdateOneAsync(
                Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
                Builders<TaskDocument>.Update
                    .Set(t => t.Reminders, kept)
                    .Set(t => t.UpdatedAt, now),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
