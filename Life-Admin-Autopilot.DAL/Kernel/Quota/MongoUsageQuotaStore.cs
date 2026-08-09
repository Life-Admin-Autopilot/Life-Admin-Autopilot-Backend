using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Quota;

/// <summary>
/// The one atomic-admission implementation, ported from the long comment in
/// <c>modules/ai/quota.ts</c>. All three Node quotas encode this identical
/// three-phase dance.
///
/// <para>
/// <b>Why the guard and the increment must be one write.</b> The old flow read
/// the count and <c>$inc</c>'d later; two concurrent requests could both pass the
/// read and both increment past the limit. Here <c>count &lt; limit</c> lives in
/// the FILTER, so only a row still under the ceiling matches and gets bumped.
/// </para>
///
/// <para>
/// <b>Why <c>upsert</c> is subtle.</b> When the row does not exist the filter
/// cannot match (no document has <c>count &lt; limit</c>), so the upsert INSERTS.
/// Mongo seeds the new document from the filter's EQUALITY fields plus the
/// <c>$inc</c>, yielding <c>count = 1</c> on first use. The <c>count</c> clause is
/// a range predicate, so it contributes nothing to the seed — which is exactly
/// what makes the insert land on 1 rather than on the limit.
/// </para>
///
/// <para>
/// <b>Why the duplicate-key retry exists.</b> Two racing upserts can both miss the
/// guard and both try to insert; the loser gets E11000. It retries as a pure
/// guarded <c>$inc</c> with no upsert — the row now exists, so the guard alone
/// decides admission.
/// </para>
///
/// <para>
/// <b>The unique index is a hard prerequisite.</b> Without it there is no E11000 to
/// catch: the once-full upsert simply inserts a SECOND counter row at 1 and the cap
/// stops applying, silently and forever. Mongoose declares the index on the schema;
/// here it comes from <c>KernelIndexProvider</c>, and a slice adding its own counter
/// collection MUST register one too. <see cref="TryAdmitAsync"/> re-reads the count
/// after an upsert-insert and refuses if the total is already at the ceiling, so a
/// missing index degrades to "correct but slower" rather than "unlimited".
/// </para>
/// </summary>
public sealed class MongoUsageQuotaStore : IUsageQuotaStore
{
    private const int DuplicateKeyErrorCode = 11000;

    private readonly IMongoDatabase _database;

    public MongoUsageQuotaStore(IMongoDatabase database)
    {
        _database = database;
    }

    public async Task<UsageQuotaAdmission> TryAdmitAsync(
        UsageQuotaBucket bucket,
        CancellationToken cancellationToken = default)
    {
        // A zero/negative ceiling can never admit — and must not be handed to the
        // upsert, which would insert a row at count 1 above a limit of 0.
        if (bucket.Limit <= 0)
        {
            return UsageQuotaAdmission.Deny(0, bucket.Limit);
        }

        var collection = Collection(bucket);
        var guarded = GuardedFilter(bucket);
        var increment = Builders<BsonDocument>.Update.Inc("count", 1);

        try
        {
            var result = await collection
                .UpdateOneAsync(guarded, increment, new UpdateOptions { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);

            if (result.ModifiedCount > 0)
            {
                return UsageQuotaAdmission.Allow(bucket.Limit);
            }

            if (result.UpsertedId is not null)
            {
                // Normally the first use of a bucket, so the total is 1. It can only
                // exceed the limit when the unique index is missing and the upsert
                // created a duplicate row — verify and roll back rather than letting
                // the cap quietly stop applying.
                var total = await ReadUsedAsync(bucket, cancellationToken).ConfigureAwait(false);
                if (total <= bucket.Limit)
                {
                    return UsageQuotaAdmission.Allow(bucket.Limit);
                }

                await ReleaseAsync(bucket, cancellationToken).ConfigureAwait(false);
                return UsageQuotaAdmission.Deny(total - 1, bucket.Limit);
            }
        }
        catch (MongoWriteException ex) when (IsDuplicateKey(ex))
        {
            var retry = await collection
                .UpdateOneAsync(guarded, increment, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (retry.ModifiedCount > 0)
            {
                return UsageQuotaAdmission.Allow(bucket.Limit);
            }
        }
        catch (MongoBulkWriteException ex) when (IsDuplicateKey(ex))
        {
            var retry = await collection
                .UpdateOneAsync(guarded, increment, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (retry.ModifiedCount > 0)
            {
                return UsageQuotaAdmission.Allow(bucket.Limit);
            }
        }

        // Nothing matched and nothing inserted: the row exists at or above the
        // ceiling. Read the live count so the upgrade prompt shows a true number;
        // fall back to the limit if the row vanished under us.
        var used = await ReadUsedAsync(bucket, cancellationToken).ConfigureAwait(false);
        return UsageQuotaAdmission.Deny(used == 0 ? bucket.Limit : used, bucket.Limit);
    }

    /// <summary>
    /// SUMS every matching row rather than reading the first. With the unique index
    /// in place there is exactly one, and the sum is that row's count; without it,
    /// summing is what keeps the reported usage honest.
    /// </summary>
    public async Task<int> ReadUsedAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default)
    {
        var rows = await Collection(bucket)
            .Find(IdentityFilter(bucket))
            .Project(Builders<BsonDocument>.Projection.Include("count"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Sum(row =>
            row.TryGetValue("count", out var count) && count.IsNumeric ? count.ToInt32() : 0);
    }

    public Task RecordAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default) =>
        Collection(bucket).UpdateOneAsync(
            IdentityFilter(bucket),
            Builders<BsonDocument>.Update.Inc("count", 1),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

    public Task ReleaseAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default) =>
        Collection(bucket).UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(IdentityFilter(bucket), Builders<BsonDocument>.Filter.Gt("count", 0)),
            Builders<BsonDocument>.Update.Inc("count", -1),
            cancellationToken: cancellationToken);

    private IMongoCollection<BsonDocument> Collection(UsageQuotaBucket bucket) =>
        _database.GetCollection<BsonDocument>(bucket.Collection);

    /// <summary>The unique key: owner plus the bucket discriminators. Equality only.</summary>
    private static FilterDefinition<BsonDocument> IdentityFilter(UsageQuotaBucket bucket)
    {
        var clauses = new List<FilterDefinition<BsonDocument>>
        {
            Builders<BsonDocument>.Filter.Eq("userId", bucket.UserId),
        };

        foreach (var (field, value) in bucket.Keys)
        {
            clauses.Add(Builders<BsonDocument>.Filter.Eq(field, value));
        }

        return Builders<BsonDocument>.Filter.And(clauses);
    }

    private static FilterDefinition<BsonDocument> GuardedFilter(UsageQuotaBucket bucket) =>
        Builders<BsonDocument>.Filter.And(
            IdentityFilter(bucket),
            Builders<BsonDocument>.Filter.Lt("count", bucket.Limit));

    private static bool IsDuplicateKey(MongoWriteException ex) =>
        ex.WriteError?.Code == DuplicateKeyErrorCode;

    private static bool IsDuplicateKey(MongoBulkWriteException ex) =>
        ex.WriteErrors.Any(e => e.Code == DuplicateKeyErrorCode);
}
