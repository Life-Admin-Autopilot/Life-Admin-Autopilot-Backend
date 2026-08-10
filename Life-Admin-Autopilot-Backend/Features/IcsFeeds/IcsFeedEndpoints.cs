using Life_Admin_Autopilot.BLL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot_Backend.Features.IcsFeeds.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.IcsFeeds;

/// <summary>
/// Ports <c>server/src/routes/me.icsFeeds.ts</c>. All four routes are
/// authenticated; NONE is rate limited in the reference, so none is here either.
/// </summary>
public static class IcsFeedEndpoints
{
    public static IEndpointRouteBuilder MapIcsFeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST /me/ics-feeds — subscribe to a calendar feed.
        //
        // Syncs IMMEDIATELY rather than waiting for the poller, because the user is
        // standing there and a subscription that shows nothing for an hour reads as
        // broken. The sync result is returned so the UI can say what actually landed,
        // including how many events were ambiguous enough to need confirming.
        endpoints.MapPost("/me/ics-feeds", async (
            HttpContext context,
            IcsFeedRepository feeds,
            IcsImportContextReader contexts,
            FeedUrlGuard guard,
            IcsFeedSyncService sync,
            CancellationToken ct) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<IcsSubscribeBody>(
                    context,
                    KernelBodyOptions.Lenient(IcsSubscribeValidator.Message, IcsSubscribeValidator.Code),
                    ct)
                .ConfigureAwait(false);

            var input = IcsSubscribeValidator.Validate(body);

            // ORDER IS OBSERVABLE, and this is the surprising half: the timezone check
            // runs BEFORE the URL is vetted, so an account with no timezone gets
            // `timezone_required` even for a URL that would have been refused as
            // unsafe. Verified live — do not reorder for "fail on the worst problem
            // first".
            var importContext = await RequireImportContextAsync(contexts, caller.Id, ct).ConfigureAwait(false);

            // Normalises webcal: and refuses anything resolving inside our own network.
            // Must happen before the URL is STORED, not merely before it is fetched —
            // otherwise the poller would happily use it later.
            Uri url;
            try
            {
                url = await guard.PrepareFeedUrlAsync(input.Url, ct).ConfigureAwait(false);
            }
            catch (UnsafeFeedUrlException ex)
            {
                throw AppException.BadRequest("unsafe_feed_url", ex.Message);
            }
            catch (Exception)
            {
                throw AppException.BadRequest("unsafe_feed_url", "That address cannot be used.");
            }

            // The NORMALISED form is what is stored, echoed and de-duplicated on — it
            // can differ from what the client sent (a bare origin gains a trailing
            // slash), and the uniqueness probe runs on this value.
            var href = url.AbsoluteUri;
            var now = DateTime.UtcNow;

            var existing = await feeds.FindByUrlAsync(caller.Id, href, ct).ConfigureAwait(false);

            // Re-subscribing updates IN PLACE. A second row for the same URL would
            // double every matter it produces.
            var feed = existing ?? await feeds
                .InsertAsync(
                    new IcsFeedDocument
                    {
                        UserId = caller.Id,
                        Url = href,
                        Label = input.Label,
                        Domain = input.Domain,
                        Status = IcsFeedVocabulary.Active,
                        FailureCount = 0,
                    },
                    now,
                    ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                existing.Label = input.Label;
                existing.Domain = input.Domain;
                await feeds.SaveAsync(existing, now, ct).ConfigureAwait(false);
            }

            var result = await sync
                .SyncAsync(
                    feed,
                    new SyncFeedOptions(importContext.Timezone, importContext.DefaultTimeOfDay),
                    ct)
                .ConfigureAwait(false);

            var payload = new IcsFeedSyncResponse { Feed = feed.ToDto(), Sync = result.ToDto() };

            // 201 for a new row, 200 for one updated in place. Both verified live.
            return existing is null
                ? Results.Created((string?)null, payload)
                : Results.Ok(payload);
        })
        .RequireAuthorization();

        // GET /me/ics-feeds — the user's subscriptions, with their health.
        endpoints.MapGet("/me/ics-feeds", async (
            HttpContext context,
            IcsFeedRepository feeds,
            CancellationToken ct) =>
        {
            var caller = context.RequireUser();

            var rows = await feeds.ListForUserAsync(caller.Id, ct).ConfigureAwait(false);

            return Results.Ok(new IcsFeedListResponse { Feeds = rows.Select(f => f.ToDto()).ToList() });
        })
        .RequireAuthorization();

        // POST /me/ics-feeds/{id}/sync — pull now.
        //
        // Exists for two reasons: a user who just fixed a broken feed should not wait
        // for the next poll, and it is the only way to exercise the pipeline by hand.
        endpoints.MapPost("/me/ics-feeds/{id}/sync", async (
            string id,
            HttpContext context,
            IcsFeedRepository feeds,
            IcsImportContextReader contexts,
            IcsFeedSyncService sync,
            CancellationToken ct) =>
        {
            var caller = context.RequireUser();
            var feedId = FeedId(id);

            var feed = await feeds.FindOwnedAsync(caller.Id, feedId, ct).ConfigureAwait(false)
                ?? throw FeedNotFound();

            // Note the order flips relative to subscribe: here the feed lookup comes
            // FIRST, so an unknown id 404s even for an account with no timezone.
            var importContext = await RequireImportContextAsync(contexts, caller.Id, ct).ConfigureAwait(false);

            var result = await sync
                .SyncAsync(
                    feed,
                    new SyncFeedOptions(importContext.Timezone, importContext.DefaultTimeOfDay),
                    ct)
                .ConfigureAwait(false);

            return Results.Ok(new IcsFeedSyncResponse { Feed = feed.ToDto(), Sync = result.ToDto() });
        })
        .RequireAuthorization();

        // DELETE /me/ics-feeds/{id} — unsubscribe.
        //
        // Also retires this feed's FUTURE OPEN matters. "Stop this calendar" plainly
        // means "stop these reminders", and leaving them behind would keep nudging the
        // user from a source they just disconnected. Past and completed matters stay:
        // they are a record of what happened.
        //
        // NOT idempotent — the second call 404s. That differs from the document-scan
        // delete, which is idempotent, and it is what the reference does.
        endpoints.MapDelete("/me/ics-feeds/{id}", async (
            string id,
            HttpContext context,
            IcsFeedRepository feeds,
            IcsMatterRepository matters,
            CancellationToken ct) =>
        {
            var caller = context.RequireUser();
            var feedId = FeedId(id);

            var feed = await feeds.FindOwnedAsync(caller.Id, feedId, ct).ConfigureAwait(false)
                ?? throw FeedNotFound();

            var now = DateTime.UtcNow;

            var retired = await matters
                .RetireFutureOpenAsync(caller.Id, feed.Id.ToString(), now, ct)
                .ConfigureAwait(false);

            await feeds.DeleteAsync(feed.Id, ct).ConfigureAwait(false);

            return Results.Ok(new IcsDeleteResponse { Removed = true, RetiredMatters = retired });
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// A malformed id is Mongoose's CastError, which the kernel renders as
    /// <c>404 not_found</c> — NOT the route's own <c>feed_not_found</c>. Verified live.
    /// </summary>
    private static ObjectId FeedId(string id) => MongoRepositoryBase<IcsFeedDocument>.ParseObjectId(id);

    private static AppException FeedNotFound() =>
        AppException.NotFound("feed_not_found", "That calendar is not connected.");

    private static async Task<IcsImportContext> RequireImportContextAsync(
        IcsImportContextReader contexts,
        ObjectId userId,
        CancellationToken cancellationToken) =>
        await contexts.ReadAsync(userId, cancellationToken).ConfigureAwait(false)
        ?? throw AppException.BadRequest(
            "timezone_required",
            "Set your timezone before subscribing to a calendar — imported events have no timezone of their own.");
}
