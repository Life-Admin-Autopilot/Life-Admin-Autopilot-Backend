using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;

namespace Life_Admin_Autopilot_Backend.Features.GoogleIntegration;

/// <summary>
/// Mirrors dated matters OUT to each user's Kitto calendar on Google.
///
/// <para>
/// Separate from <see cref="GoogleSyncWorker"/> and far more frequent, because the
/// two directions have opposite tolerances. An import that lags an hour costs
/// nothing — the events were already in the user's calendar. A matter the user just
/// saved and cannot find on their phone reads as "it didn't work", so this runs
/// every half minute.
/// </para>
///
/// <para>
/// <b>Why a worker rather than pushing inline from the write path.</b> A Google
/// round trip inside <c>POST /me/tasks</c> would put an external network call on the
/// latency of every save, and make Google being slow look like Kitto being slow.
/// Worse, it would have to be repeated identically in the chat agent's tools, the
/// document review commit, the voice commit and every bulk action — five places to
/// forget. The push reads desired state from the rows instead, so a matter created
/// by ANY path is mirrored without that path knowing this exists.
/// </para>
/// </summary>
internal sealed class GooglePushWorker : KernelPollingWorker
{
    private const int Batch = 25;

    private readonly TimeProvider _clock;

    public GooglePushWorker(IServiceProvider services, ILogger<GooglePushWorker> logger, TimeProvider clock)
        : base(services, logger)
    {
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override string WorkerName => "google-push";

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        using var scope = Services.CreateScope();
        var provider = scope.ServiceProvider;

        var repository = provider.GetRequiredService<IIntegrationRepository>();
        var push = provider.GetRequiredService<IGoogleCalendarPushService>();

        var candidates = await repository.FindPollCandidatesAsync(Batch, cancellationToken).ConfigureAwait(false);

        foreach (var integration in candidates)
        {
            try
            {
                var result = await push.PushAsync(integration, now, cancellationToken).ConfigureAwait(false);

                if (result.Created + result.Updated + result.Removed > 0)
                {
                    Logger.LogInformation(
                        "googlePush:pushed integrationId={IntegrationId} created={Created} updated={Updated} removed={Removed}",
                        integration.Id,
                        result.Created,
                        result.Updated,
                        result.Removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's broken grant must not stop the rest of the batch. The
                // connection service already demotes a revoked grant to
                // needs_reauth, so this only has to keep the loop alive.
                Logger.LogWarning(
                    ex,
                    "googlePush:failed integrationId={IntegrationId}",
                    integration.Id);
            }
        }
    }
}
