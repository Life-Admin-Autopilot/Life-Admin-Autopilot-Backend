using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;

namespace Life_Admin_Autopilot_Backend.Features.Notifications;

/// <summary>
/// Port of <c>server/src/lib/reminderWorker.ts</c>'s scheduler half — a 30-second
/// poll around <see cref="ReminderTick"/>.
///
/// <para>
/// <b>The double-send guard is the per-entry claim, not this class.</b>
/// <see cref="Life_Admin_Autopilot.DAL.Features.Notifications.ReminderTaskRepository.TryClaimReminderAsync"/>
/// stamps <c>firedAt</c> in the same round trip that matches it as null, so a slow
/// or overlapping tick that re-reads the same task simply loses the claim and
/// writes nothing. The kernel's overlap guard is a second line of defence;
/// correctness does not depend on it.
/// </para>
///
/// <para>Reminders are coarse, so a 30s poll is plenty.</para>
///
/// <para>
/// <b>A tick can legitimately abort.</b>
/// <see cref="ReminderNotificationText.ShortDate"/> throws on an unrecognised
/// timezone, mirroring the uncaught <c>Intl</c> RangeError in Node, where the
/// tick's <c>.catch</c> logs it and the remaining tasks — and the clarification
/// settlement — are skipped until the next poll. That is ported rather than
/// defended against: a silent UTC fallback would name the wrong DAY for anyone
/// east or west of it, which is worse than naming none (KERNEL.md §8.1). The
/// kernel base class catches and logs, so the worker itself survives.
/// </para>
/// </summary>
internal sealed class ReminderWorker : KernelPollingWorker
{
    public ReminderWorker(IServiceProvider services, ILogger<ReminderWorker> logger)
        : base(services, logger)
    {
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override string WorkerName => "reminder";

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // A BackgroundService is a singleton, so the scope belongs to the tick.
        using var scope = Services.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<ReminderTick>()
            .RunAsync(DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }
}
