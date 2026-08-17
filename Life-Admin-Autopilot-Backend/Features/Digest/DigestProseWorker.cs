using Life_Admin_Autopilot.BLL.Features.Digest;
using Life_Admin_Autopilot.DAL.Features.Digest;

namespace Life_Admin_Autopilot_Backend.Features.Digest;

/// <summary>
/// Drains <see cref="DigestProseQueue"/>: one model call per job, then a
/// fingerprint-guarded patch of the cached headline.
///
/// <para>
/// <b>Not a <c>KernelPollingWorker</c>.</b> That base class exists for work that has
/// to be discovered — a table scanned on a timer for rows that became due. This work
/// announces itself, so the loop waits on the channel instead and starts the instant
/// a dashboard load queues something. A poll here would add its own interval to
/// every user's wait for no benefit.
/// </para>
///
/// <para>
/// <b>Serial by construction.</b> One reader, one job at a time. The sentence is the
/// lowest-value thing the server produces and it shares a rate-limited free-tier key
/// with the proposal extractor, which a user is actively waiting on — so this yields
/// throughput rather than competing for quota. The queue is bounded and lossy for
/// the same reason.
/// </para>
/// </summary>
internal sealed class DigestProseWorker : BackgroundService
{
    private readonly DigestProseQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<DigestProseWorker> _logger;

    public DigestProseWorker(
        DigestProseQueue queue,
        IServiceProvider services,
        ILogger<DigestProseWorker> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. The flag is cleared in the finally below, and the row keeps
                // the computed headline — which is true, just plainer.
                return;
            }
            catch (Exception ex)
            {
                // The loop must outlive any one job: a throw here would end prose for
                // the whole process, and the failure only ever costs one sentence.
                _logger.LogWarning(ex, "daily-digest:prose-job-failed localDate={LocalDate}", job.LocalDate);
            }
            finally
            {
                // ALWAYS, and whatever happened. This is what the dashboard's poll is
                // waiting on — a job that fails silently without clearing its flag
                // leaves the client refetching for a write that is never coming.
                _queue.Complete(job);
            }
        }
    }

    private async Task RunAsync(DigestProseJob job, CancellationToken cancellationToken)
    {
        // A BackgroundService is a singleton; the scoped repository belongs to the job.
        using var scope = _services.CreateScope();
        var provider = scope.ServiceProvider;

        var headline = await provider
            .GetRequiredService<DigestProseWriter>()
            .WriteAsync(job, cancellationToken)
            .ConfigureAwait(false);

        // Written back even when the model produced NOTHING. The null is not a no-op:
        // it stamps the attempt, and that stamp is the only thing standing between a
        // day the model is down and a request per dashboard load. Only an exception —
        // which the caller logs and does not stamp — earns a retry.
        var patched = await provider
            .GetRequiredService<DailyDigestRepository>()
            .CompleteProseAsync(
                job.UserId,
                job.LocalDate,
                job.SourceHash,
                headline,
                DateTime.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        if (!patched)
        {
            // The user changed something while the model was writing, so the row has
            // already been rebuilt against newer facts and a fresh job is queued for
            // it. Dropping this sentence is the correct outcome, not a failure.
            _logger.LogDebug("daily-digest:prose-superseded localDate={LocalDate}", job.LocalDate);
        }
    }
}
