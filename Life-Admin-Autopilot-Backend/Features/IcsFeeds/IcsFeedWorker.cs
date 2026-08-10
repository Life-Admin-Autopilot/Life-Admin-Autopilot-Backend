using Life_Admin_Autopilot.BLL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;

namespace Life_Admin_Autopilot_Backend.Features.IcsFeeds;

/// <summary>
/// Port of <c>server/src/lib/icsFeedWorker.ts</c> — the background poller for
/// subscribed calendar feeds.
///
/// <para>
/// Mirrors the reminder worker's shape (a polled interval with a small batch) but
/// the PACING is very different, because ICS is not a live protocol. A school-term
/// or bin-collection feed changes perhaps three times a year, and every poll in
/// between is a conditional GET that ends in a 304 with no body. Polling more often
/// would buy nothing and cost the publisher bandwidth, so the tick is frequent (to
/// spread work out) while each individual feed is only eligible every few hours.
/// </para>
/// </summary>
internal sealed class IcsFeedWorker : KernelPollingWorker
{
    /// <summary>How often a HEALTHY feed is re-read.</summary>
    private static readonly TimeSpan FeedInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Small batch: each feed is a network round trip and there is no deadline
    /// pressure — a feed read twenty minutes late is indistinguishable from one read
    /// on time.
    /// </summary>
    private const int Batch = 10;

    /// <summary>
    /// Failing feeds back off exponentially so a dead URL is not hammered every six
    /// hours forever. Capped, so a feed that comes back is picked up within a day.
    /// </summary>
    private const int MaxBackoffSteps = 4;

    public IcsFeedWorker(IServiceProvider services, ILogger<IcsFeedWorker> logger)
        : base(services, logger)
    {
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override string WorkerName => "ics-feed";

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        using var scope = Services.CreateScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IcsFeedRepository>();
        var contexts = scope.ServiceProvider.GetRequiredService<IcsImportContextReader>();
        var sync = scope.ServiceProvider.GetRequiredService<IcsFeedSyncService>();

        // 'gone' is terminal — a 404 does not start working again on its own and the
        // user has already been told. Re-polling it forever would be pure waste.
        var candidates = await feeds.FindPollCandidatesAsync(Batch * 4, cancellationToken).ConfigureAwait(false);

        var due = candidates
            .Where(feed => NextDueAt(feed) <= now)
            .Take(Batch)
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        // One lookup for the batch rather than per feed — a household's feeds cluster
        // onto very few users.
        var byUser = await contexts
            .ReadManyAsync(due.Select(f => f.UserId).Distinct().ToList(), cancellationToken)
            .ConfigureAwait(false);

        foreach (var feed in due)
        {
            // No device is present during a poll, so a missing timezone cannot be
            // guessed — the resolver would throw. Mark it plainly rather than failing
            // in a loop the user cannot see.
            if (!byUser.TryGetValue(feed.UserId, out var context))
            {
                feed.LastFetchedAt = now;
                feed.Status = IcsFeedVocabulary.Error;
                feed.LastError = "Set your timezone to keep this calendar in sync.";
                await feeds.SaveAsync(feed, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var result = await sync
                    .SyncAsync(feed, new SyncFeedOptions(context.Timezone, context.DefaultTimeOfDay, now), cancellationToken)
                    .ConfigureAwait(false);

                if (result.Status == "synced")
                {
                    Logger.LogInformation(
                        "icsFeedWorker:synced feedId={FeedId} created={Created} updated={Updated} needingConfirmation={Needing}",
                        feed.Id,
                        result.Created,
                        result.Updated,
                        result.NeedingConfirmation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The sync service already records feed-level failures; this catches
                // anything unexpected so one bad feed cannot end the tick for the rest.
                Logger.LogError(ex, "icsFeedWorker:feed-failed feedId={FeedId}", feed.Id);
            }
        }
    }

    /// <summary>When this feed next becomes eligible, given how badly it has been failing.</summary>
    private static DateTime NextDueAt(IcsFeedDocument feed)
    {
        var last = feed.LastFetchedAt ?? DateTime.UnixEpoch;
        var steps = Math.Min(feed.FailureCount, MaxBackoffSteps);
        return last + FeedInterval * Math.Pow(2, steps);
    }
}
