using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <param name="ExternalId">Stable per-item id from the source. Must not change between polls.</param>
/// <param name="Kind">
/// <c>reminder</c> fires; <c>list</c> is passive. An item whose time had to be
/// ASSUMED must arrive as <c>list</c> — ask rather than act on a guess, and a
/// passive matter is what "ask" looks like before the user answers.
/// </param>
/// <param name="Completed">Source says this is finished. Completed items are never resurrected as open.</param>
/// <param name="SourceHasOwnAlerts">
/// The source will already alert the user at the deadline itself — a calendar event
/// carrying its own overrides, for instance. When set, Kitto drops its own at-due
/// nudge and keeps only the lead-time one, because an integration that adds a
/// second alert ten minutes before every appointment is exactly the notification
/// pile-up the reminder design exists to prevent. The lead-time nudge is kept
/// because it is genuinely additive.
/// </param>
public readonly record struct ExternalMatterInput(
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
/// Port of <c>server/src/modules/integrations/reconcileMatter.ts</c> — the three
/// rules for turning an external item into a matter, in one place.
///
/// <list type="number">
///   <item>
///     <b>Upsert on (userId, externalSource, externalId), never a blind insert.</b>
///     Nothing we sync from has reliable push, so every importer re-reads an
///     overlapping window. A blind insert is how one school assembly becomes a
///     matter every hour.
///   </item>
///   <item>
///     <b>A matter the user deleted STAYS deleted.</b> Task soft-deletes, so an
///     importer that ignored <c>deletedAt</c> would resurrect everything the user
///     swept away — and they would have to sweep it again after every poll, forever.
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
/// <b>Duplication note for the merge.</b> In the reference this module is shared
/// between the ICS and Google importers — but the ICS <c>syncFeed</c> does NOT call
/// it, it inlines the same three rules. This port keeps the shared module (so the
/// Google slice has an identical counterpart to consolidate with) and drives it from
/// the ICS sync with <c>Completed</c> and <c>SourceHasOwnAlerts</c> both false,
/// which is behaviourally identical to the reference's inlined ICS copy.
/// </para>
/// </summary>
public sealed class IcsMatterReconciler
{
    /// <summary>
    /// Under a minute apart is the same moment — sources round differently, and a
    /// re-plan on every poll would clear <c>firedAt</c> and re-fire nudges the user
    /// has already had.
    /// </summary>
    private static readonly TimeSpan SameMoment = TimeSpan.FromMilliseconds(60_000);

    private readonly IcsMatterRepository _matters;
    private readonly ReminderPlanner _reminders;

    public IcsMatterReconciler(IcsMatterRepository matters, ReminderPlanner reminders)
    {
        _matters = matters;
        _reminders = reminders;
    }

    public async Task<ReconcileOutcome> ReconcileAsync(
        ExternalMatterInput input,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var existing = await _matters
            .FindByExternalIdAsync(input.UserId, input.ExternalId, cancellationToken)
            .ConfigureAwait(false);

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
                Id = ObjectId.GenerateNewId(),
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
            };

            await _matters.InsertAsync(task, now, cancellationToken).ConfigureAwait(false);
            await _reminders.SetRulesRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
            await DropDuplicateDueNudgeAsync(task, input, cancellationToken).ConfigureAwait(false);

            return new ReconcileOutcome(true, false);
        }

        // Completion propagates in ONE direction only: the source can close a matter,
        // never reopen one. A user who ticked something off in Kitto must not have it
        // un-ticked by a stale row upstream.
        if (input.Completed && existing.Status != "done")
        {
            existing.Status = "done";
            existing.CompletedAt = now;
            await _matters.SetCompletedAsync(existing, now, cancellationToken).ConfigureAwait(false);
            return new ReconcileOutcome(false, true);
        }

        // Rule 3.
        var unchanged = existing.DueAt is { } due
            && (due - input.DueAt).Duration() <= SameMoment
            && existing.Kind == input.Kind;

        if (unchanged)
        {
            return new ReconcileOutcome(false, false);
        }

        existing.DueAt = input.DueAt;
        existing.Kind = input.Kind;
        existing.TimePrecision = input.TimePrecision;
        existing.Confidence = input.Confidence;

        await _matters.UpdateTimingAsync(existing, now, cancellationToken).ConfigureAwait(false);

        // Re-plan ONLY because the deadline actually moved — the planner clears
        // firedAt, so calling it on an unchanged task would re-fire nudges the user
        // already received.
        await _reminders.SetRulesRemindersAsync(existing, now, cancellationToken).ConfigureAwait(false);
        await DropDuplicateDueNudgeAsync(existing, input, cancellationToken).ConfigureAwait(false);

        return new ReconcileOutcome(false, true);
    }

    /// <summary>
    /// The planner always writes an at-due entry, which is right for a matter Kitto
    /// owns and wrong for one the source already alerts on. Stripping it afterwards
    /// keeps the lead-time logic in exactly one place rather than teaching the
    /// planner about integrations.
    /// </summary>
    private async Task DropDuplicateDueNudgeAsync(
        TaskDocument task,
        ExternalMatterInput input,
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
        await _matters.SetRemindersAsync(task, kept, cancellationToken).ConfigureAwait(false);
    }
}
