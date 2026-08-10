using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.IcsFeeds;

/// <summary>
/// Every read and write of the <c>icsfeeds</c> collection.
///
/// <para>
/// Feeds are NOT soft-deleted — <c>DELETE /me/ics-feeds/{id}</c> removes the row
/// outright, which is why the endpoint is not idempotent. That is deliberate and
/// verified live; see <see cref="DeleteAsync"/>.
/// </para>
/// </summary>
public sealed class IcsFeedRepository : MongoRepositoryBase<IcsFeedDocument>
{
    public IcsFeedRepository(IMongoDatabase database)
        : base(database, MongoCollections.IcsFeeds)
    {
    }

    /// <summary>The uniqueness probe behind "re-subscribing updates in place".</summary>
    public Task<IcsFeedDocument?> FindByUrlAsync(
        ObjectId userId,
        string normalizedUrl,
        CancellationToken cancellationToken = default) =>
        Collection
            .Find(Filter.And(UserScoped(userId), Filter.Eq(f => f.Url, normalizedUrl)))
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task<IcsFeedDocument?> FindOwnedAsync(
        ObjectId userId,
        ObjectId feedId,
        CancellationToken cancellationToken = default) =>
        Collection
            .Find(Filter.And(UserScoped(userId), Filter.Eq(f => f.Id, feedId)))
            .FirstOrDefaultAsync(cancellationToken)!;

    /// <summary>
    /// <c>IcsFeed.find({userId}).sort({createdAt:-1})</c>. No limit, no pagination —
    /// a household has a handful of subscriptions, not a feed of them.
    /// </summary>
    public async Task<IReadOnlyList<IcsFeedDocument>> ListForUserAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(UserScoped(userId))
            .Sort(Sort.Descending(f => f.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Mongoose's <c>timestamps: true</c> stamps both on insert. Reproduced here
    /// rather than left to the caller, because <c>createdAt</c> is the list's sort
    /// key and a missing value would silently reorder the whole page.
    /// </summary>
    public async Task<IcsFeedDocument> InsertAsync(
        IcsFeedDocument feed,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (feed.Id == ObjectId.Empty)
        {
            feed.Id = ObjectId.GenerateNewId();
        }

        feed.CreatedAt = now;
        feed.UpdatedAt = now;

        await Collection.InsertOneAsync(feed, cancellationToken: cancellationToken).ConfigureAwait(false);
        return feed;
    }

    /// <summary>
    /// Mongoose's <c>doc.save()</c>: whole-document write plus a fresh
    /// <c>updatedAt</c>.
    ///
    /// <para>
    /// A replace rather than a field-by-field <c>$set</c> because
    /// <c>lastError</c> / <c>etag</c> / <c>lastModified</c> must be able to go from
    /// present to ABSENT — Mongoose stores <c>undefined</c> by omitting the key, and
    /// the kernel's <c>IgnoreIfNull</c> convention reproduces that only on a
    /// full-document write.
    /// </para>
    /// </summary>
    public async Task SaveAsync(
        IcsFeedDocument feed,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        feed.UpdatedAt = now;
        await Collection
            .ReplaceOneAsync(
                Filter.Eq(f => f.Id, feed.Id),
                feed,
                new ReplaceOptions { IsUpsert = false },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A hard delete, matching Node's <c>feed.deleteOne()</c>. The second call
    /// therefore 404s — unlike the document-scan delete, which IS idempotent.
    /// </summary>
    public Task DeleteAsync(ObjectId feedId, CancellationToken cancellationToken = default) =>
        Collection.DeleteOneAsync(Filter.Eq(f => f.Id, feedId), cancellationToken);

    /// <summary>
    /// The poller's candidate scan: oldest-fetched first, skipping the terminal
    /// <c>gone</c> state. Over-fetches so the caller can apply the per-feed backoff
    /// in memory, exactly as Node does.
    /// </summary>
    public async Task<IReadOnlyList<IcsFeedDocument>> FindPollCandidatesAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.In(f => f.Status, new[] { IcsFeedVocabulary.Active, IcsFeedVocabulary.Error }))
            .Sort(Sort.Ascending(f => f.LastFetchedAt))
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
