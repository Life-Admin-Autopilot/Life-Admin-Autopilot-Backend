using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Clarifications;

/// <summary>
/// The terminal state change a route applies to a held question.
///
/// <para>
/// A <c>null</c> member means "not in this patch", never "set it to null" — Node's
/// <c>closeOut</c> only ever ADDS fields, and none of the four routes clears one.
/// </para>
/// </summary>
public readonly record struct ClarificationPatch(
    string? Status = null,
    DateTime? ResolvedAt = null,
    string? Answer = null,
    DateTime? DeferredUntil = null);

/// <summary>
/// Ports the reads and the one write of <c>server/src/routes/me.clarifications.ts</c>.
/// </summary>
public sealed class ClarificationRepository : MongoRepositoryBase<ClarificationDocument>
{
    /// <summary>
    /// <c>PAGE_SIZE</c>. The route fetches <c>PAGE_SIZE + 1</c> so it can answer
    /// <c>hasMore</c> honestly instead of silently truncating at a round number.
    /// </summary>
    public const int PageSize = 50;

    /// <summary><c>DEFER_MS</c> — how long a skipped item stays out of sight.</summary>
    public static readonly TimeSpan DeferWindow = TimeSpan.FromDays(7);

    public ClarificationRepository(IMongoDatabase database)
        : base(database, MongoCollections.Clarifications)
    {
    }

    /// <summary>
    /// One page of the card stack, newest first.
    ///
    /// <para>
    /// Composes <see cref="MongoRepositoryBase{TDocument}.VisibleOpen"/> rather than a
    /// bare <c>status == "open"</c>. <c>TaskCountsService</c> and the digest compose
    /// the same predicate; they disagreed once, which is why it lives in the base.
    /// </para>
    /// </summary>
    /// <param name="before">
    /// Exclusive <c>createdAt</c> upper bound — the cursor. Null means the first page.
    /// </param>
    /// <returns>
    /// Up to <c>PageSize + 1</c> rows. The extra one is the probe; the caller slices it off.
    /// </returns>
    public Task<List<ClarificationDocument>> FindVisibleOpenPageAsync(
        ObjectId userId,
        DateTime? before,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var filter = Filter.And(UserScoped(userId), VisibleOpen(now));

        if (before.HasValue)
        {
            filter = Filter.And(filter, Filter.Lt(c => c.CreatedAt, before.Value));
        }

        return Collection
            .Find(filter)
            .Sort(Sort.Descending(c => c.CreatedAt))
            .Limit(PageSize + 1)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// <c>Clarification.create(...)</c> — the one INSERT, used by the hold route.
    ///
    /// <para>
    /// The caller sets <c>createdAt</c>/<c>updatedAt</c> itself: Mongoose stamps them
    /// from <c>timestamps: true</c> and the .NET driver stamps nothing, so a document
    /// handed here already carries both. Same for <c>__v</c>.
    /// </para>
    /// </summary>
    public Task InsertAsync(ClarificationDocument document, CancellationToken cancellationToken = default) =>
        Collection.InsertOneAsync(document, cancellationToken: cancellationToken);

    /// <summary>
    /// <c>countOpenClarifications</c> — a bare <c>{userId, status:'open'}</c> count,
    /// backing the create route's queue cap.
    ///
    /// <para>
    /// <b>Deliberately not <c>VisibleOpen()</c>.</b> Everything that lists or counts
    /// held items FOR DISPLAY composes that predicate; this is backpressure, and a
    /// question the user deferred is still queued and still returns, so it still
    /// occupies a slot.
    /// </para>
    /// </summary>
    public Task<long> CountOpenAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        Collection.CountDocumentsAsync(
            Filter.And(UserScoped(userId), Filter.Eq(c => c.Status, ClarificationVocabulary.Open)),
            cancellationToken: cancellationToken);

    /// <summary>
    /// <c>findOne({_id, userId})</c>. Note there is NO status filter and no
    /// <c>VisibleOpen</c> here — resolve/defer/drop must be able to find an
    /// already-closed row so they can answer idempotently instead of 404ing.
    /// </summary>
    public Task<ClarificationDocument?> FindOwnedAsync(
        ObjectId userId,
        ObjectId id,
        CancellationToken cancellationToken = default) =>
        Collection
            .Find(Filter.And(Filter.Eq(c => c.Id, id), UserScoped(userId)))
            .FirstOrDefaultAsync(cancellationToken)!;

    /// <summary>
    /// Port of the route's <c>closeOut</c> — an atomic <c>$set</c>, deliberately NOT
    /// a document save.
    ///
    /// <para>
    /// <c>save()</c> revalidates the WHOLE document and <c>taskId</c> is required, so
    /// rows written before that field existed threw a ValidationError on every
    /// terminal action and 500'd — the question could never be cleared and came back
    /// on every reload. A <c>$set</c> touches only the fields being set, so a legacy
    /// row can still be closed out.
    /// </para>
    ///
    /// <para>
    /// <b>Two timestamp behaviours that pull in opposite directions, both required.</b>
    /// Mongoose's <c>timestamps: true</c> injects <c>updatedAt</c> into the <c>$set</c>
    /// of an <c>updateOne</c>; the .NET driver does not, so it is added by hand here or
    /// the stored row goes stale. But <c>doc.set(patch)</c> updates only the PATCH
    /// fields in memory — not <c>updatedAt</c> — and the route echoes that in-memory
    /// document. So the response carries the PRE-update <c>updatedAt</c> alongside
    /// current patched fields. Both halves are ported literally.
    /// </para>
    /// </summary>
    public async Task CloseOutAsync(
        ClarificationDocument doc,
        ClarificationPatch patch,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var set = new BsonDocument();

        if (patch.Status is not null)
        {
            set["status"] = patch.Status;
        }

        if (patch.Answer is not null)
        {
            set["answer"] = patch.Answer;
        }

        if (patch.ResolvedAt.HasValue)
        {
            set["resolvedAt"] = patch.ResolvedAt.Value;
        }

        if (patch.DeferredUntil.HasValue)
        {
            set["deferredUntil"] = patch.DeferredUntil.Value;
        }

        // Mongoose adds this itself. The driver does not — see the note above.
        set["updatedAt"] = now;

        await Collection
            .UpdateOneAsync(
                Filter.Eq(c => c.Id, doc.Id),
                new BsonDocumentUpdateDefinition<ClarificationDocument>(new BsonDocument("$set", set)),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // `doc.set(patch)` — the patch fields only. UpdatedAt is left alone ON PURPOSE.
        if (patch.Status is not null)
        {
            doc.Status = patch.Status;
        }

        if (patch.Answer is not null)
        {
            doc.Answer = patch.Answer;
        }

        if (patch.ResolvedAt.HasValue)
        {
            doc.ResolvedAt = patch.ResolvedAt.Value;
        }

        if (patch.DeferredUntil.HasValue)
        {
            doc.DeferredUntil = patch.DeferredUntil.Value;
        }
    }
}
