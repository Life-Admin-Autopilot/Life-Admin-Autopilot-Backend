using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;

namespace Life_Admin_Autopilot_Backend.Features.GoogleIntegration;

/// <summary>
/// Background poller for connected Google accounts. Port of
/// <c>server/src/lib/googleSyncWorker.ts</c>.
///
/// <para>
/// Calendar has push notifications, but they are NOT a substitute for this loop:
/// watch channels expire in about 7 days with no auto-renewal, and a missed renewal
/// kills the change stream with no user-visible error — reminders simply stop
/// arriving. Google's own sync guidance is to reconcile out of band for exactly that
/// reason. Tasks has no push at all, so polling is its only option.
/// </para>
///
/// <para>
/// Every tick is cheap even when nothing changed: Calendar answers a syncToken
/// request with an empty list, and Tasks is filtered by <c>updatedMin</c>.
/// </para>
///
/// <para>
/// Skipped entirely when <c>Kernel:Workers:Enabled</c> is false, which mirrors
/// Node's <c>NODE_ENV === 'test'</c> early return and is what the <c>:4200</c>
/// reference does.
/// </para>
/// </summary>
internal sealed class GoogleSyncWorker : KernelPollingWorker
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(60);
    private const int Batch = 10;
    private const int MaxBackoffSteps = 4;

    private readonly TimeProvider _clock;

    public GoogleSyncWorker(IServiceProvider services, ILogger<GoogleSyncWorker> logger, TimeProvider clock)
        : base(services, logger)
    {
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override string WorkerName => "google-sync";

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        using var scope = Services.CreateScope();
        var provider = scope.ServiceProvider;

        var repository = provider.GetRequiredService<IIntegrationRepository>();
        var profiles = provider.GetRequiredService<IGoogleImportProfileReader>();
        var calendarSync = provider.GetRequiredService<IGoogleCalendarSyncService>();
        var tasksSync = provider.GetRequiredService<IGoogleTasksSyncService>();

        var candidates = await repository.FindPollCandidatesAsync(Batch * 4, cancellationToken).ConfigureAwait(false);

        var due = candidates
            .Where(integration => NextDueAt(integration) <= now)
            .Take(Batch)
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        var byUser = await profiles
            .FindManyAsync(due.Select(i => i.UserId).Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);

        foreach (var integration in due)
        {
            byUser.TryGetValue(integration.UserId, out var profile);

            // No device is present during a poll, so a missing timezone cannot be
            // guessed — every date-only Google Task would resolve against the wrong
            // day.
            if (string.IsNullOrEmpty(profile.Timezone))
            {
                integration.LastError = "Set your timezone to keep importing from Google.";
                integration.CalendarSyncedAt = now;
                integration.UpdatedAt = now;
                await repository.SaveAsync(integration, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var options = new GoogleSyncOptions(profile.Timezone!, profile.DefaultTimeOfDay, integration.ImportDomain);

            try
            {
                var calendar = await calendarSync
                    .SyncAsync(integration, options, now, cancellationToken)
                    .ConfigureAwait(false);

                var tasks = await tasksSync
                    .SyncAsync(integration, options, now, cancellationToken)
                    .ConfigureAwait(false);

                integration.TasksSyncedAt = now;
                integration.LastError = null;
                integration.UpdatedAt = now;
                await repository.SaveAsync(integration, cancellationToken).ConfigureAwait(false);

                Logger.LogInformation(
                    "googleSyncWorker:synced integrationId={IntegrationId} created={Created} updated={Updated} commitments={Commitments} fullResync={FullResync}",
                    integration.Id,
                    calendar.Created + tasks.Created,
                    calendar.Updated + tasks.Updated,
                    calendar.Commitments,
                    calendar.FullResync);
            }
            catch (IntegrationUnavailableException)
            {
                // GetAccessTokenAsync already flipped the row to needs_reauth when
                // Google said invalid_grant, so there is nothing left to record.
                Logger.LogInformation(
                    "googleSyncWorker:needs-reauth integrationId={IntegrationId}",
                    integration.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "googleSyncWorker:failed integrationId={IntegrationId}", integration.Id);
                integration.LastError = "Last import failed. Kitto will try again.";
                integration.CalendarSyncedAt = now;
                integration.UpdatedAt = now;
                await repository.SaveAsync(integration, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <c>needs_reauth</c> is never polled — the candidate query filters it out — so
    /// backoff only has to pace transient failures.
    /// </summary>
    private static DateTime NextDueAt(IntegrationDocument integration)
    {
        var last = integration.CalendarSyncedAt ?? DateTime.UnixEpoch;
        var steps = string.IsNullOrEmpty(integration.LastError) ? 0 : Math.Min(MaxBackoffSteps, 2);
        return last + (SyncInterval * Math.Pow(2, steps));
    }
}
