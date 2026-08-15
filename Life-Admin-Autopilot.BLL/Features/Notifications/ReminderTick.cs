using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
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
