using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Telemetry;

/// <summary>
/// Indexes for the two telemetry collections.
///
/// <para>
/// Element names are camelCase here for the same reason the aggregation pipelines
/// are: <c>MongoKernelConventions</c> registers
/// <c>CamelCaseElementNameConvention</c> globally, so an index declared on
/// <c>Day</c> would be created on a field that does not exist and would never be
/// used — the query would still return correct answers, just by collection scan,
/// which is the kind of failure nobody notices until it is a production incident.
/// </para>
/// </summary>
public sealed class AiUsageIndexes : IMongoIndexProvider
{
    /// <summary>
    /// How long a raw event survives. Rollups are permanent, so this only bounds how
    /// far back a single expensive turn can be traced — not how far back the console
    /// can chart.
    /// </summary>
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(90);

    public string Name => "ai-usage";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var events = database.GetCollection<BsonDocument>(TelemetryCollections.AiUsageEvents);
        var rollups = database.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups);

        // The rollup job's only query.
        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["day"] = 1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // "Show me this user's most recent expensive turns."
        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["userId"] = 1, ["at"] = -1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // TTL. Watches `expiresAt` rather than `at` so the retention window can be
        // varied per write — a future "keep this turn, it is evidence" flag only has
        // to push one document's date out, with no migration and no second index.
        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { ["expiresAt"] = 1 },
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // UNIQUE, and load-bearing: RollupDayAsync deletes-then-inserts a day, and
        // two overlapping runs of the job would otherwise leave two rows for the same
        // {user, day, feature} and double every figure that sums them.
        await rollups.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { ["day"] = 1, ["userId"] = 1, ["feature"] = 1 },
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Every console window filter leads with the day range.
        await rollups.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["day"] = 1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // The customer-detail cost panel.
        await rollups.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { ["userId"] = 1, ["day"] = 1 }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
