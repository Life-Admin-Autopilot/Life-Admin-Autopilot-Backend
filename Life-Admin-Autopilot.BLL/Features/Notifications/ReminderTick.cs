using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Notifications;

/// <summary>
/// One pass of <c>reminderWorker.ts</c>'s <c>runOnce()</c>: claim and fire every
/// due reminder, then settle clarifications nobody answered.
///
/// <para>
/// Separate from the <c>IHostedService</c> that schedules it so a test can drive a
/// tick directly instead of waiting on a 30-second timer. The scheduler owns
/// <i>when</i>; this owns <i>what</i>.
/// </para>
/// </summary>
public sealed class ReminderTick
{
    private readonly ReminderTaskRepository _tasks;
    private readonly NotificationRepository _notifications;
    private readonly ReminderUserTimezoneReader _timezones;
    private readonly StaleClarificationSettler _clarifications;
    private readonly INotificationService _push;
    private readonly IDocumentScanNotifications _preferences;
    private readonly ILogger<ReminderTick> _logger;

    public ReminderTick(
        ReminderTaskRepository tasks,
        NotificationRepository notifications,
        ReminderUserTimezoneReader timezones,
        StaleClarificationSettler clarifications,
        INotificationService push,
        IDocumentScanNotifications preferences,
        ILogger<ReminderTick> logger)
    {
        _tasks = tasks;
        _notifications = notifications;
        _timezones = timezones;
        _clarifications = clarifications;
        _push = push;
        _preferences = preferences;
        _logger = logger;
    }

    /// <summary>
    /// One reminder entry paired with the matter it belongs to, scored once so the
    /// sort does not recompute it per comparison.
    /// </summary>
    private sealed record PendingReminder(TaskDocument Matter, ReminderEntryDocument Reminder)
    {
        public double Score { get; } = ReminderUrgency.Score(
            new ReminderTaskShape(Matter.Title, Matter.Domain, Matter.Kind, Matter.DueAt),
            Matter.Priority,
            Reminder.At);
    }

    /// <summary>
    /// Drops sub-millisecond precision, because BSON's DateTime carries none: an
    /// in-memory stamp that still has it would compare as distinct here and land
    /// identical in Mongo, which is the whole failure this guards against.
    /// </summary>
    private static DateTime TruncateToMillisecond(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Kind);

    /// <summary>Number of notifications actually written. Diagnostic; Node returns nothing.</summary>
    public async Task<int> RunAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var due = await _tasks.FindDueBatchAsync(now, cancellationToken).ConfigureAwait(false);

        // One query for the whole batch rather than a User lookup per task — a tick
        // can carry 100 tasks and they cluster onto far fewer accounts.
        var zones = await _timezones
            .ReadAsync(due.Select(t => t.UserId).Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);

        var fired = 0;

        // Flattened across tasks BEFORE anything is written, because the ranking below
        // has to see the whole batch: a tick that ordered each task's entries
        // separately would still hand the user its tasks in Mongo's order.
        //
        // The batch spans every account, and mixing them is harmless — a stable sort
        // over the whole list leaves each user's own reminders in the same relative
        // order they would have had alone, and nobody ever sees another account's row.
        var ready = due
            .SelectMany(task => task.Reminders
                // Re-filter in memory: the $elemMatch selected the TASK, so the document
                // still carries its already-fired and not-yet-due entries.
                .Where(r => r.FiredAt is null && r.At <= now)
                .Select(reminder => new PendingReminder(task, reminder)))
            .ToList();

        // Least urgent first. The list is deliberately reversed relative to what the
        // user sees — see ReminderUrgency.DeliveryOrder for why.
        var ordered = ReminderUrgency.DeliveryOrder(ready, p => p.Score, p => p.Reminder.At);

        // The high-water mark that keeps the ranking above from being thrown away.
        var lastWrittenAt = DateTime.MinValue;

        foreach (var (task, reminder) in ordered)
        {
            var timeZone = zones.TryGetValue(task.UserId, out var zone)
                ? zone
                : ReminderUserTimezoneReader.DefaultTimezone;

            // Atomically claim THIS entry before writing anything. If another
            // tick got there first the claim fails and we skip — no double-send.
            var won = await _tasks
                .TryClaimReminderAsync(task.Id, reminder.At, now, cancellationToken)
                .ConfigureAwait(false);

            if (!won)
            {
                continue;
            }

            // `timestamps: true` stamps at SAVE time, not at the top of the tick, so
            // a batch carries distinct createdAt values — USUALLY.
            //
            // "Usually" is not good enough here, and this was measured rather than
            // reasoned about. The feed sorts on {createdAt: -1} with no tie-break, so
            // two rows sharing a stamp come back in whatever order Mongo chose, and
            // the ranking above is silently discarded. Against a local Mongo two
            // consecutive claims-and-writes routinely finish inside the SAME
            // millisecond — which is BSON's resolution — so the denser the batch, the
            // likelier the ordering is lost. Dense batches are the only ones where
            // ordering matters at all.
            //
            // So the stamp is made strictly increasing across the batch: truthful
            // whenever the clock has moved on, nudged forward a millisecond when it
            // has not. It never moves backwards and never lands in the past.
            var writtenAt = TruncateToMillisecond(DateTime.UtcNow);
            if (writtenAt <= lastWrittenAt)
            {
                writtenAt = lastWrittenAt.AddMilliseconds(1);
            }

            lastWrittenAt = writtenAt;

            var body = ReminderNotificationText.Body(task.DueAt, reminder.Kind, timeZone);

            await _notifications.InsertAsync(
                new NotificationDocument
                {
                    Id = ObjectId.GenerateNewId(),
                    UserId = task.UserId,
                    Kind = "reminder",
                    TaskId = task.Id,
                    Title = task.Title,
                    Body = body,
                    CreatedAt = writtenAt,
                    UpdatedAt = writtenAt,
                },
                cancellationToken).ConfigureAwait(false);

            await DeliverAsync(task, body, cancellationToken).ConfigureAwait(false);

            fired++;
        }

        await _clarifications.SettleStaleAsync(now, cancellationToken).ConfigureAwait(false);

        return fired;
    }

    /// <summary>
    /// Send the fired reminder to the user's registered devices.
    ///
    /// <para>
    /// The notification row above is what the in-app list reads, and writing it was
    /// all this tick used to do — which meant a reminder only ever appeared if the
    /// user opened the app and looked. A reminder nobody is told about is the one
    /// thing the product exists to prevent, so the row is the record and this is the
    /// delivery.
    /// </para>
    ///
    /// <para>
    /// <b>Best effort, and deliberately after the write.</b> The claim has already
    /// been taken and the row already exists, so a provider outage must not fail the
    /// tick, roll anything back, or re-fire a reminder that was legitimately claimed.
    /// A user with no device registered is not an error either — the service logs
    /// that case itself rather than treating silence as success.
    /// </para>
    /// </summary>
    private async Task DeliverAsync(TaskDocument task, string body, CancellationToken cancellationToken)
    {
        try
        {
            // The same preference the document-scan notifier honours. Someone who
            // turned push off still gets the in-app row; they just are not buzzed.
            if (!await _preferences.WantsPushAsync(task.UserId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // The device row is keyed by the JWT subject, which SessionService signs
            // as the Mongo user id — so this string, not the Identity user id.
            await _push
                .SendToUserAsync(
                    task.UserId.ToString(),
                    new PushMessage(
                        task.Title,
                        body,
                        new Dictionary<string, string>
                        {
                            // Lets the app open the matter the reminder is about
                            // instead of dumping the user on the dashboard.
                            ["kind"] = "reminder",
                            ["taskId"] = task.Id.ToString(),
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "reminder:push-failed taskId={TaskId} userId={UserId}",
                task.Id,
                task.UserId);
        }
    }
}
