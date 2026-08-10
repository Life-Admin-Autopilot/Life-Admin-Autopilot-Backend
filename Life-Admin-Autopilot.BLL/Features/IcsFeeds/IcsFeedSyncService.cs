using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <param name="Timezone">The user's IANA zone. REQUIRED — a poll has no device to fall back on.</param>
/// <param name="DefaultTimeOfDay">The user's 'HH:mm' import default, for all-day events.</param>
public readonly record struct SyncFeedOptions(string Timezone, string? DefaultTimeOfDay, DateTime? Now = null);

/// <param name="Status"><c>unchanged</c> | <c>synced</c> | <c>gone</c> | <c>error</c>.</param>
/// <param name="NeedingConfirmation">
/// Occurrences filed as passive <c>list</c> items because their time was ambiguous.
/// </param>
public readonly record struct SyncFeedResult(
    string Status,
    int Created,
    int Updated,
    int NeedingConfirmation,
    string? Reason = null);

/// <summary>
/// Port of <c>server/src/modules/integrations/ics/syncFeed.ts</c> — pull a feed and
/// reconcile it into matters.
///
/// <para>
/// The reconcile rules live in <see cref="ExternalMatterReconciler"/>, shared with
/// the Google importers — the reference inlines them here instead, which is
/// behaviourally identical for this caller (see that type's remarks). What is here is
/// the feed-health state machine: which outcome sets which status, when the failure
/// counter moves, and when the cache validators are replaced.
/// </para>
/// </summary>
public sealed class IcsFeedSyncService
{
    /// <summary>
    /// Look slightly BACK so an event that started yesterday is still reconciled, and
    /// a year FORWARD because school terms and renewals are published far ahead.
    /// </summary>
    private const int WindowBackDays = 7;

    private const int WindowForwardDays = 365;

    private readonly FeedFetcher _fetcher;
    private readonly IcsFeedRepository _feeds;
    private readonly ExternalMatterReconciler _reconciler;
    private readonly ILogger<IcsFeedSyncService> _logger;

    public IcsFeedSyncService(
        FeedFetcher fetcher,
        IcsFeedRepository feeds,
        ExternalMatterReconciler reconciler,
        ILogger<IcsFeedSyncService> logger)
    {
        _fetcher = fetcher;
        _feeds = feeds;
        _reconciler = reconciler;
        _logger = logger;
    }

    public async Task<SyncFeedResult> SyncAsync(
        IcsFeedDocument feed,
        SyncFeedOptions options,
        CancellationToken cancellationToken = default)
    {
        var now = options.Now ?? DateTime.UtcNow;

        var response = await _fetcher
            .FetchAsync(feed.Url, new FeedCacheState(feed.Etag, feed.LastModified), cancellationToken)
            .ConfigureAwait(false);

        feed.LastFetchedAt = now;

        if (response.Status == FeedFetchStatus.Unchanged)
        {
            // The common case, and the reason polling is affordable: no body, no
            // parse, no writes. A previously failing feed that answers 304 is healthy
            // again.
            feed.Status = IcsFeedVocabulary.Active;
            feed.FailureCount = 0;
            feed.LastError = null;
            await _feeds.SaveAsync(feed, now, cancellationToken).ConfigureAwait(false);

            return new SyncFeedResult("unchanged", 0, 0, 0);
        }

        if (response.Status is FeedFetchStatus.Gone or FeedFetchStatus.Error)
        {
            feed.FailureCount += 1;
            feed.LastError = response.Reason;

            // 'gone' is TERMINAL — a 404 will not start working again on its own, and
            // the user needs to be told rather than left with silently frozen events.
            feed.Status = response.Status == FeedFetchStatus.Gone
                ? IcsFeedVocabulary.Gone
                : IcsFeedVocabulary.Error;

            await _feeds.SaveAsync(feed, now, cancellationToken).ConfigureAwait(false);

            var status = response.Status == FeedFetchStatus.Gone ? "gone" : "error";
            return new SyncFeedResult(status, 0, 0, 0, response.Reason);
        }

        IReadOnlyList<IcsOccurrence> occurrences;
        try
        {
            occurrences = IcsEventParser.Parse(
                response.Body ?? string.Empty,
                new ParseIcsContext(
                    new IcsTimeContext(options.Timezone, options.DefaultTimeOfDay),
                    now.AddDays(-WindowBackDays),
                    now.AddDays(WindowForwardDays)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ics:parse-failed feedId={FeedId}", feed.Id);

            feed.FailureCount += 1;
            feed.Status = IcsFeedVocabulary.Error;
            feed.LastError = "That feed could not be read.";
            await _feeds.SaveAsync(feed, now, cancellationToken).ConfigureAwait(false);

            return new SyncFeedResult("error", 0, 0, 0, feed.LastError);
        }

        var created = 0;
        var updated = 0;
        var needingConfirmation = 0;

        foreach (var occurrence in occurrences)
        {
            if (occurrence.When.NeedsConfirmation)
            {
                needingConfirmation += 1;
            }

            // Namespace the feed's own UID under the subscription id. UIDs are only
            // required to be unique WITHIN a calendar, so two councils can and do both
            // ship "1" — unnamespaced they would collide on the
            // (userId, externalSource, externalId) unique index and one feed would
            // silently overwrite the other. It also makes "retire this feed's matters"
            // an indexed prefix query instead of an unscoped sweep.
            var externalId = IcsMatterRepository.ExternalIdFor(feed.Id.ToString(), occurrence.ExternalId);

            // An ambiguous time must NOT fire. `list` is the model's passive kind — it
            // shows on the home list and never nudges — which is what "surface it, ask,
            // do not act on a guess" looks like below the confidence threshold.
            var kind = occurrence.When.NeedsConfirmation ? "list" : "reminder";

            var outcome = await _reconciler
                .ReconcileAsync(
                    new ExternalMatterInput(
                        feed.UserId,
                        IcsFeedVocabulary.ExternalSource,
                        externalId,
                        occurrence.Summary,
                        feed.Domain,
                        occurrence.When.DueAt,
                        kind,
                        occurrence.When.Precision,
                        occurrence.When.Confidence,
                        occurrence.Location),
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Created)
            {
                created += 1;
            }
            else if (outcome.Updated)
            {
                updated += 1;
            }
        }

        feed.Etag = response.Etag;
        feed.LastModified = response.LastModified;
        feed.LastSyncedAt = now;
        feed.Status = IcsFeedVocabulary.Active;
        feed.FailureCount = 0;
        feed.LastError = null;
        await _feeds.SaveAsync(feed, now, cancellationToken).ConfigureAwait(false);

        return new SyncFeedResult("synced", created, updated, needingConfirmation);
    }
}
