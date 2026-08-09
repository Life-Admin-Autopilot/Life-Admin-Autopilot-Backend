using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Mongo;

/// <summary>
/// One slice's Mongo indexes.
///
/// <para>
/// Mongoose creates indexes from the schema declaration; the .NET driver creates
/// nothing. Some of those indexes are not an optimisation but a CORRECTNESS
/// requirement — the quota primitive's duplicate-key retry only works because a
/// unique index exists to produce the duplicate-key error. Without it a racing
/// upsert inserts a SECOND counter row and the cap silently stops applying.
/// </para>
///
/// <para><b>Registration:</b> <c>services.AddMongoIndexProvider&lt;MyIndexes&gt;();</c>
/// from your <c>AddXxxFeature()</c>. Index creation is idempotent, so re-declaring
/// an existing index is free.</para>
/// </summary>
public interface IMongoIndexProvider
{
    string Name { get; }

    Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates every registered provider's indexes once at startup. Failures are
/// logged, never fatal — a server that cannot create an index must still boot and
/// serve reads.
/// </summary>
public sealed class MongoIndexInitializer
{
    private readonly IMongoDatabase _database;
    private readonly IEnumerable<IMongoIndexProvider> _providers;
    private readonly ILogger<MongoIndexInitializer> _logger;

    public MongoIndexInitializer(
        IMongoDatabase database,
        IEnumerable<IMongoIndexProvider> providers,
        ILogger<MongoIndexInitializer> logger)
    {
        _database = database;
        _providers = providers;
        _logger = logger;
    }

    public async Task EnsureAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                await provider.EnsureAsync(_database, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "mongo:index-create-failed provider={Provider}", provider.Name);
            }
        }
    }
}

/// <summary>
/// Indexes the kernel owns: the three usage counters, plus the uniqueness and
/// lookup indexes the kernel's own services depend on.
///
/// <para>Slices add their own provider rather than editing this one.</para>
/// </summary>
public sealed class KernelIndexProvider : IMongoIndexProvider
{
    public string Name => "kernel";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        // --- Usage counters. UNIQUE is load-bearing, not an optimisation. -----
        await UniqueAsync(
            database,
            MongoCollections.AiUsageCounters,
            new BsonDocument { ["userId"] = 1, ["date"] = 1, ["kind"] = 1 },
            cancellationToken).ConfigureAwait(false);

        await UniqueAsync(
            database,
            MongoCollections.DocumentScanUsageCounters,
            new BsonDocument { ["userId"] = 1, ["month"] = 1 },
            cancellationToken).ConfigureAwait(false);

        await UniqueAsync(
            database,
            MongoCollections.TranslationUsageCounters,
            new BsonDocument { ["userId"] = 1, ["month"] = 1, ["locale"] = 1 },
            cancellationToken).ConfigureAwait(false);

        // --- Users -------------------------------------------------------------
        await UniqueAsync(
            database,
            MongoCollections.Users,
            new BsonDocument { ["email"] = 1 },
            cancellationToken).ConfigureAwait(false);

        await UniqueAsync(
            database,
            MongoCollections.Users,
            new BsonDocument { ["identityUserId"] = 1 },
            cancellationToken).ConfigureAwait(false);

        // --- Tasks. The Matters list leads with the two fields present in EVERY
        //     query, so it stays usable whichever optional filters are applied.
        await PlainAsync(
            database,
            MongoCollections.Tasks,
            new BsonDocument { ["userId"] = 1, ["deletedAt"] = 1, ["status"] = 1, ["dueAt"] = 1 },
            cancellationToken).ConfigureAwait(false);

        await PlainAsync(
            database,
            MongoCollections.Tasks,
            new BsonDocument { ["userId"] = 1, ["tags"] = 1 },
            cancellationToken).ConfigureAwait(false);

        // Reminder-worker claim: open tasks with an un-fired reminder due now.
        await PlainAsync(
            database,
            MongoCollections.Tasks,
            new BsonDocument { ["status"] = 1, ["reminders.firedAt"] = 1, ["reminders.at"] = 1 },
            cancellationToken).ConfigureAwait(false);

        // --- Clarifications: the home banner / card-stack query. ---------------
        await PlainAsync(
            database,
            MongoCollections.Clarifications,
            new BsonDocument { ["userId"] = 1, ["status"] = 1, ["createdAt"] = -1 },
            cancellationToken).ConfigureAwait(false);

        // Idempotency for voice-born holds. PARTIAL, so the many chat-born holds
        // (which carry no sourceKey) do not all collide on null.
        await CreateAsync(
            database,
            MongoCollections.Clarifications,
            new BsonDocument { ["userId"] = 1, ["sourceKey"] = 1 },
            new CreateIndexOptions<BsonDocument>
            {
                Unique = true,
                PartialFilterExpression = new BsonDocument("sourceKey", new BsonDocument("$type", "string")),
            },
            cancellationToken).ConfigureAwait(false);

        // --- Notifications: feed + unread count. -------------------------------
        await PlainAsync(
            database,
            MongoCollections.Notifications,
            new BsonDocument { ["userId"] = 1, ["readAt"] = 1, ["createdAt"] = -1 },
            cancellationToken).ConfigureAwait(false);

        // --- Bulk ops: run history, the single-open-categorize guard, and the TTL.
        await PlainAsync(
            database,
            MongoCollections.TaskBulkOps,
            new BsonDocument { ["userId"] = 1, ["createdAt"] = -1 },
            cancellationToken).ConfigureAwait(false);

        await CreateAsync(
            database,
            MongoCollections.TaskBulkOps,
            new BsonDocument { ["userId"] = 1, ["kind"] = 1 },
            new CreateIndexOptions<BsonDocument>
            {
                Unique = true,
                PartialFilterExpression = new BsonDocument("status", "proposed"),
            },
            cancellationToken).ConfigureAwait(false);

        await CreateAsync(
            database,
            MongoCollections.TaskBulkOps,
            new BsonDocument { ["expiresAt"] = 1 },
            new CreateIndexOptions<BsonDocument> { ExpireAfter = TimeSpan.Zero },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the unique index a counter collection needs. Public so a slice
    /// introducing its own quota bucket can call it directly instead of copying the
    /// options.
    /// </summary>
    public static Task EnsureQuotaIndexAsync(
        IMongoDatabase database,
        UsageQuotaBucket bucket,
        CancellationToken cancellationToken = default)
    {
        var keys = new BsonDocument { ["userId"] = 1 };
        foreach (var field in bucket.Keys.Keys)
        {
            keys[field] = 1;
        }

        return UniqueAsync(database, bucket.Collection, keys, cancellationToken);
    }

    private static Task UniqueAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument keys,
        CancellationToken cancellationToken) =>
        CreateAsync(database, collection, keys, new CreateIndexOptions<BsonDocument> { Unique = true }, cancellationToken);

    private static Task PlainAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument keys,
        CancellationToken cancellationToken) =>
        CreateAsync(database, collection, keys, new CreateIndexOptions<BsonDocument>(), cancellationToken);

    private static async Task CreateAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument keys,
        CreateIndexOptions<BsonDocument> options,
        CancellationToken cancellationToken)
    {
        var model = new CreateIndexModel<BsonDocument>(new BsonDocumentIndexKeysDefinition<BsonDocument>(keys), options);
        await database
            .GetCollection<BsonDocument>(collection)
            .Indexes.CreateOneAsync(model, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
