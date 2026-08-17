using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Admin;

/// <summary>How the console asks for a page of customers.</summary>
/// <param name="Search">Matches email or display name, anchored. Null shows everyone.</param>
/// <param name="OnlySuspended">Restricts to suspended accounts.</param>
/// <param name="OnlyUnverified">Restricts to accounts that never confirmed their email.</param>
/// <param name="OnlyNeverOnboarded">Restricts to accounts that signed up and stopped.</param>
/// <param name="RestrictTo">
/// An id set computed elsewhere — the usage-derived segments (cost outliers, heavy
/// users) are resolved against the rollups first and handed down as ids, because
/// spend does not live in this collection.
/// </param>
public readonly record struct AdminCustomerQuery(
    string? Search = null,
    bool OnlySuspended = false,
    bool OnlyUnverified = false,
    bool OnlyNeverOnboarded = false,
    DateTime? CreatedBefore = null,
    IReadOnlyCollection<ObjectId>? RestrictTo = null,
    string SortBy = AdminCustomerSort.CreatedAt,
    bool Descending = true,
    int Skip = 0,
    int Take = 50);

/// <summary>The sortable columns. A value outside this set falls back rather than throwing.</summary>
public static class AdminCustomerSort
{
    public const string CreatedAt = "createdAt";
    public const string Email = "email";
    public const string UpdatedAt = "updatedAt";

    public static string Normalize(string? candidate) => candidate switch
    {
        Email => Email,
        UpdatedAt => UpdatedAt,
        _ => CreatedAt,
    };
}

/// <summary>One row of the customer table. Usage columns are joined on in the service.</summary>
public sealed record AdminCustomerRow(
    ObjectId Id,
    Guid IdentityUserId,
    string Email,
    string? DisplayName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? EmailVerifiedAt,
    DateTime? SuspendedAt,
    string? SuspendedReason,
    string Tier,
    string? Locale,
    string? Timezone,
    bool HasOnboarded);

public sealed record AdminCustomerPage(IReadOnlyList<AdminCustomerRow> Rows, long Total);

/// <summary>Per-customer counts, gathered in one pass rather than N queries.</summary>
public sealed record AdminCustomerCounts(int Matters, int OpenMatters, int Documents, int Conversations);

public interface IAdminCustomerRepository
{
    Task<AdminCustomerPage> SearchAsync(AdminCustomerQuery query, CancellationToken cancellationToken = default);

    Task<UserProfileDocument?> FindAsync(ObjectId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many customers match, ignoring paging entirely.
    ///
    /// <para>
    /// Separate from <see cref="SearchAsync"/> because a caller that needs the TRUE
    /// size of a set must not get a page size. Broadcast is the reason this exists:
    /// reusing the paged search made the recipient count cap out at the page limit,
    /// so a segment of 4,000 reported "200 recipients", the safety cap never fired,
    /// and a send reached 200 people while reporting success.
    /// </para>
    /// </summary>
    Task<long> CountAsync(AdminCustomerQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of matching customers, up to <paramref name="limit"/>, ignoring Skip/Take.
    /// Projects the id alone, so a large set costs one small document each.
    /// </summary>
    Task<IReadOnlyList<ObjectId>> IdsAsync(
        AdminCustomerQuery query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Emails for a set of ids, for tables that lead with usage rather than with the user.</summary>
    Task<IReadOnlyDictionary<ObjectId, string>> EmailsForAsync(
        IReadOnlyCollection<ObjectId> ids,
        CancellationToken cancellationToken = default);

    Task<AdminCustomerCounts> CountsForAsync(ObjectId id, CancellationToken cancellationToken = default);

    /// <summary>Signups per UTC day across a window, gap-free.</summary>
    Task<IReadOnlyDictionary<string, int>> SignupsByDayAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    Task<long> TotalCustomersAsync(CancellationToken cancellationToken = default);

    Task SetSuspendedAsync(
        ObjectId id,
        DateTime? suspendedAt,
        string? reason,
        DateTime now,
        CancellationToken cancellationToken = default);

    Task SetTierAsync(ObjectId id, string tier, DateTime? renewsAt, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop every usage-counter row this user has, across all three buckets.
    ///
    /// <para>
    /// Deleting rather than zeroing on purpose: the quota primitive's upsert seeds a
    /// fresh row from the filter's equality fields on next use, so absence and zero
    /// are the same state to every reader — and absence cannot leave a stale
    /// <c>limit</c> from an old tier behind.
    /// </para>
    /// </summary>
    Task<long> ResetQuotasAsync(ObjectId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdminCustomerRepository"/>
public sealed class AdminCustomerRepository : IAdminCustomerRepository
{
    /// <summary>A page size no caller can talk us past.</summary>
    public const int MaxTake = 200;

    private readonly IMongoDatabase _database;

    public AdminCustomerRepository(IMongoDatabase database)
    {
        _database = database;
    }

    private IMongoCollection<UserProfileDocument> Users =>
        _database.GetCollection<UserProfileDocument>(MongoCollections.Users);

    public async Task<AdminCustomerPage> SearchAsync(
        AdminCustomerQuery query,
        CancellationToken cancellationToken = default)
    {
        var filter = BuildFilter(query);

        var total = await Users.CountDocumentsAsync(filter, options: null, cancellationToken)
            .ConfigureAwait(false);

        var sortField = AdminCustomerSort.Normalize(query.SortBy);
        var sort = query.Descending
            ? Builders<UserProfileDocument>.Sort.Descending(sortField)
            : Builders<UserProfileDocument>.Sort.Ascending(sortField);

        var docs = await Users
            .Find(filter)
            .Sort(sort)
            .Skip(Math.Max(0, query.Skip))
            .Limit(Math.Clamp(query.Take, 1, MaxTake))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AdminCustomerPage(docs.Select(ToRow).ToList(), total);
    }

    private static FilterDefinition<UserProfileDocument> BuildFilter(AdminCustomerQuery query)
    {
        var b = Builders<UserProfileDocument>.Filter;
        var filter = b.Empty;

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Anchored and escaped. Unanchored would be a collection scan on the one
            // collection that grows with every signup; unescaped would let a search
            // box become a regex denial of service.
            var pattern = new BsonRegularExpression("^" + Regex.Escape(query.Search.Trim()), "i");
            filter &= b.Or(b.Regex(u => u.Email, pattern), b.Regex(u => u.DisplayName, pattern));
        }

        // `Ne(null)` rather than `Exists` — a document written before the field
        // existed has no key at all, and Exists(false) would sweep in every
        // pre-suspension account as though it were explicitly not suspended.
        filter &= query.OnlySuspended
            ? b.Ne(u => u.SuspendedAt, null)
            : b.Empty;

        if (query.OnlyUnverified)
        {
            filter &= b.Eq(u => u.EmailVerifiedAt, null);
        }

        if (query.OnlyNeverOnboarded)
        {
            filter &= b.Eq(u => u.HasOnboarded, false);
        }

        if (query.CreatedBefore is { } before)
        {
            filter &= b.Lt(u => u.CreatedAt, before);
        }

        if (query.RestrictTo is { Count: > 0 } ids)
        {
            filter &= b.In(u => u.Id, ids);
        }
        else if (query.RestrictTo is { Count: 0 })
        {
            // An empty restriction is "nothing matched the segment", NOT "no
            // restriction". Without this branch a segment that found no users would
            // silently render the entire customer base.
            filter &= b.Eq(u => u.Id, ObjectId.Empty);
        }

        return filter;
    }

    public async Task<UserProfileDocument?> FindAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        await Users.Find(u => u.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public Task<long> CountAsync(AdminCustomerQuery query, CancellationToken cancellationToken = default) =>
        Users.CountDocumentsAsync(BuildFilter(query), options: null, cancellationToken);

    public async Task<IReadOnlyList<ObjectId>> IdsAsync(
        AdminCustomerQuery query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var ids = await Users
            .Find(BuildFilter(query))
            .Project(u => u.Id)
            .Limit(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return ids;
    }

    public async Task<IReadOnlyDictionary<ObjectId, string>> EmailsForAsync(
        IReadOnlyCollection<ObjectId> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<ObjectId, string>();
        }

        var docs = await Users
            .Find(Builders<UserProfileDocument>.Filter.In(u => u.Id, ids))
            .Project(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return docs.ToDictionary(d => d.Id, d => d.Email);
    }

    public async Task<AdminCustomerCounts> CountsForAsync(
        ObjectId id,
        CancellationToken cancellationToken = default)
    {
        var tasks = _database.GetCollection<BsonDocument>(MongoCollections.Tasks);
        var documents = _database.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments);
        var conversations = _database.GetCollection<BsonDocument>(MongoCollections.AiConversations);

        // Soft deletes are stored by ABSENCE of the key on live rows, so the filter
        // has to be `$exists: false` rather than `= null` — see NotDeleted() in the
        // task repository, and the IgnoreIfNull convention that makes it necessary.
        var live = new BsonDocument
        {
            ["userId"] = id,
            ["deletedAt"] = new BsonDocument("$exists", false),
        };

        var open = new BsonDocument(live) { ["status"] = "open" };

        var counts = await Task.WhenAll(
            tasks.CountDocumentsAsync(live, options: null, cancellationToken),
            tasks.CountDocumentsAsync(open, options: null, cancellationToken),
            documents.CountDocumentsAsync(new BsonDocument("userId", id), options: null, cancellationToken),
            conversations.CountDocumentsAsync(new BsonDocument("userId", id), options: null, cancellationToken))
            .ConfigureAwait(false);

        return new AdminCustomerCounts(
            (int)counts[0],
            (int)counts[1],
            (int)counts[2],
            (int)counts[3]);
    }

    public async Task<IReadOnlyDictionary<string, int>> SignupsByDayAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("createdAt", new BsonDocument
            {
                ["$gte"] = from,
                ["$lt"] = to,
            })),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = new BsonDocument("$dateToString", new BsonDocument
                {
                    ["format"] = "%Y-%m-%d",
                    ["date"] = "$createdAt",
                    ["timezone"] = "UTC",
                }),
                ["count"] = new BsonDocument("$sum", 1),
            }),
        };

        var rows = await Users
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r["_id"].AsString, r => r["count"].ToInt32(), StringComparer.Ordinal);
    }

    public Task<long> TotalCustomersAsync(CancellationToken cancellationToken = default) =>
        Users.CountDocumentsAsync(Builders<UserProfileDocument>.Filter.Empty, options: null, cancellationToken);

    /// <summary>
    /// Suspend or restore.
    ///
    /// <para>
    /// <b>Restore <c>$unset</c>s rather than setting null.</b> The
    /// <c>IgnoreIfNull</c> convention governs how a document is SERIALISED, not what
    /// an <c>UpdateDefinition</c> writes — <c>$set</c> with null stores a literal
    /// BSON null. That would leave a restored account carrying a null
    /// <c>suspendedAt</c> where every other cleared field in this database is simply
    /// absent, and any future filter written as <c>$exists</c> (which is how soft
    /// deletes are already expressed here) would then read it as still suspended.
    /// </para>
    /// </summary>
    public Task SetSuspendedAsync(
        ObjectId id,
        DateTime? suspendedAt,
        string? reason,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<UserProfileDocument>.Update;

        var update = suspendedAt is null
            ? builder
                .Unset(u => u.SuspendedAt)
                .Unset(u => u.SuspendedReason)
                .Set(u => u.UpdatedAt, now)
            : builder
                .Set(u => u.SuspendedAt, suspendedAt)
                .Set(u => u.SuspendedReason, reason)
                .Set(u => u.UpdatedAt, now);

        return Users.UpdateOneAsync(u => u.Id == id, update, options: null, cancellationToken);
    }

    public Task SetTierAsync(
        ObjectId id,
        string tier,
        DateTime? renewsAt,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<UserProfileDocument>.Update
            .Set(u => u.Subscription.Tier, tier)
            .Set(u => u.Subscription.RenewsAt, renewsAt)
            .Set(u => u.Subscription.CanceledAt, null)
            .Set(u => u.UpdatedAt, now);

        return Users.UpdateOneAsync(u => u.Id == id, update, options: null, cancellationToken);
    }

    public async Task<long> ResetQuotasAsync(ObjectId id, CancellationToken cancellationToken = default)
    {
        var collections = new[]
        {
            MongoCollections.AiUsageCounters,
            MongoCollections.DocumentScanUsageCounters,
            MongoCollections.TranslationUsageCounters,
        };

        var filter = new BsonDocument("userId", id);
        var removed = 0L;

        foreach (var name in collections)
        {
            var result = await _database
                .GetCollection<BsonDocument>(name)
                .DeleteManyAsync(filter, cancellationToken)
                .ConfigureAwait(false);

            removed += result.DeletedCount;
        }

        return removed;
    }

    private static AdminCustomerRow ToRow(UserProfileDocument u) => new(
        u.Id,
        u.IdentityUserId,
        u.Email,
        u.DisplayName,
        u.CreatedAt,
        u.UpdatedAt,
        u.EmailVerifiedAt,
        u.SuspendedAt,
        u.SuspendedReason,
        u.Subscription.Tier,
        u.Locale,
        u.Timezone,
        u.HasOnboarded);
}
