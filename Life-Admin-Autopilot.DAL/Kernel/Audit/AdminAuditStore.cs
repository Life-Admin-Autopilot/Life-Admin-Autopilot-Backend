using Life_Admin_Autopilot.DAL.Kernel.Activity;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Audit;

/// <summary>How the console asks for a page of the log.</summary>
/// <param name="ActorId">Filter to one admin.</param>
/// <param name="TargetUserId">Filter to one customer.</param>
/// <param name="ActionPrefix">
/// Matches the <c>subject.</c> half — <c>customer.</c> returns every customer action.
/// Anchored, so it cannot be turned into an expensive unanchored scan.
/// </param>
public readonly record struct AdminAuditQuery(
    Guid? ActorId = null,
    string? TargetUserId = null,
    string? ActionPrefix = null,
    DateTime? From = null,
    DateTime? To = null,
    int Skip = 0,
    int Take = 50);

/// <summary>One page, plus the count the pager needs.</summary>
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditEventDocument> Rows, long Total);

/// <summary>
/// Append and read. <b>There is deliberately no update and no delete</b> — see
/// <see cref="AdminAuditEventDocument"/>.
/// </summary>
public interface IAdminAuditStore
{
    Task AppendAsync(AdminAuditEventDocument entry, CancellationToken cancellationToken = default);

    Task<AdminAuditPage> QueryAsync(AdminAuditQuery query, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdminAuditStore"/>
public sealed class MongoAdminAuditStore : IAdminAuditStore
{
    /// <summary>A page size no caller can talk us past.</summary>
    public const int MaxTake = 200;

    private readonly IMongoDatabase _database;
    private readonly IAdminActivityBus _activity;

    /// <summary>
    /// <paramref name="activity"/> is optional so the many tests that construct
    /// this store directly keep compiling; DI always supplies one.
    /// </summary>
    public MongoAdminAuditStore(IMongoDatabase database, IAdminActivityBus? activity = null)
    {
        _database = database;
        _activity = activity ?? new AdminActivityBus();
    }

    private IMongoCollection<AdminAuditEventDocument> Collection =>
        _database.GetCollection<AdminAuditEventDocument>(AuditCollections.AdminAuditEvents);

    /// <summary>
    /// <b>This one is allowed to throw.</b> Unlike usage telemetry — which must never
    /// fail a turn — an admin mutation whose audit row could not be written must not
    /// be reported as having succeeded. The caller writes the audit entry first and
    /// lets the failure abort the action.
    /// </summary>
    public async Task AppendAsync(
        AdminAuditEventDocument entry,
        CancellationToken cancellationToken = default)
    {
        await Collection.InsertOneAsync(entry, options: null, cancellationToken).ConfigureAwait(false);

        // The durable write happens first. The live feed is a courtesy on top and
        // must never be the reason an audited action fails — Publish swallows.
        _activity.Publish(
            AdminActivityKind.AdminAction,
            $"{entry.ActorEmail} — {Humanise(entry.Action)}",
            entry.Outcome == AdminAuditOutcome.Ok
                ? AdminActivitySeverity.Notice
                : AdminActivitySeverity.Warning,
            detail: entry.Reason,
            userId: entry.TargetUserId,
            email: entry.TargetEmail);
    }

    /// <summary>`customer.quota_reset` reads as "customer quota reset" in the feed.</summary>
    private static string Humanise(string action) =>
        action.Replace('.', ' ').Replace('_', ' ');

    public async Task<AdminAuditPage> QueryAsync(
        AdminAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<AdminAuditEventDocument>.Filter;
        var filter = builder.Empty;

        if (query.ActorId is { } actor)
        {
            filter &= builder.Eq(e => e.ActorId, actor);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetUserId))
        {
            filter &= builder.Eq(e => e.TargetUserId, query.TargetUserId);
        }

        if (!string.IsNullOrWhiteSpace(query.ActionPrefix))
        {
            // Anchored and escaped. An unanchored pattern here would be a collection
            // scan on a collection that only ever grows, and an unescaped one would
            // let a filter string become a regex denial of service.
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(query.ActionPrefix);
            filter &= builder.Regex(e => e.Action, new BsonRegularExpression(pattern));
        }

        if (query.From is { } from)
        {
            filter &= builder.Gte(e => e.At, from);
        }

        if (query.To is { } to)
        {
            filter &= builder.Lt(e => e.At, to);
        }

        var total = await Collection.CountDocumentsAsync(filter, options: null, cancellationToken)
            .ConfigureAwait(false);

        var rows = await Collection
            .Find(filter)
            .SortByDescending(e => e.At)
            .Skip(Math.Max(0, query.Skip))
            .Limit(Math.Clamp(query.Take, 1, MaxTake))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AdminAuditPage(rows, total);
    }
}

/// <summary>
/// Indexes for the audit log.
///
/// <para>
/// <b>No TTL index, on purpose.</b> Every other collection in this system has one;
/// this is the exception, and it is the whole retention policy.
/// </para>
/// </summary>
public sealed class AdminAuditIndexes : IMongoIndexProvider
{
    public string Name => "admin-audit";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var collection = database.GetCollection<BsonDocument>(AuditCollections.AdminAuditEvents);

        // The default view: newest first.
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["at"] = -1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // "Everything that ever happened to this customer" — the first thing anyone
        // asks when an account looks wrong.
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["targetUserId"] = 1, ["at"] = -1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // "Everything this admin has done" — the access review.
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["actorId"] = 1, ["at"] = -1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["action"] = 1, ["at"] = -1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
