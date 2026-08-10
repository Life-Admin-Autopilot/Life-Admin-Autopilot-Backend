using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
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

    public ReminderTick(
        ReminderTaskRepository tasks,
        NotificationRepository notifications,
        ReminderUserTimezoneReader timezones,
        StaleClarificationSettler clarifications)
    {
        _tasks = tasks;
        _notifications = notifications;
        _timezones = timezones;
        _clarifications = clarifications;
    }

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

        foreach (var task in due)
        {
            var timeZone = zones.TryGetValue(task.UserId, out var zone)
                ? zone
                : ReminderUserTimezoneReader.DefaultTimezone;

            // Re-filter in memory: the $elemMatch selected the TASK, so the document
            // still carries its already-fired and not-yet-due entries.
            var ready = task.Reminders.Where(r => r.FiredAt is null && r.At <= now).ToList();

            foreach (var reminder in ready)
            {
                // Atomically claim THIS entry before writing anything. If another
                // tick got there first the claim fails and we skip — no double-send.
                var won = await _tasks
                    .TryClaimReminderAsync(task.Id, reminder.At, now, cancellationToken)
                    .ConfigureAwait(false);

                if (!won)
                {
                    continue;
                }

                // `timestamps: true` stamps at SAVE time, not at the top of the tick,
                // so a batch carries distinct createdAt values.
                var writtenAt = DateTime.UtcNow;

                await _notifications.InsertAsync(
                    new NotificationDocument
                    {
                        Id = ObjectId.GenerateNewId(),
                        UserId = task.UserId,
                        Kind = "reminder",
                        TaskId = task.Id,
                        Title = task.Title,
                        Body = ReminderNotificationText.Body(task.DueAt, reminder.Kind, timeZone),
                        CreatedAt = writtenAt,
                        UpdatedAt = writtenAt,
                    },
                    cancellationToken).ConfigureAwait(false);

                fired++;
            }
        }

        await _clarifications.SettleStaleAsync(now, cancellationToken).ConfigureAwait(false);

        return fired;
    }
}
