using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Telemetry;

/// <summary>
/// The one <see cref="IAiUsageStore"/>.
///
/// <para>
/// <b>Writes go to <c>aiusageevents</c>; every read goes to
/// <c>aiusagerollups</c>.</b> That split is the whole performance story. A raw
/// event row exists so one expensive turn can be traced back to its conversation;
/// a dashboard that scanned them would be fine on day one and time out by month
/// three. Nothing in this class reads the event collection except
/// <see cref="RollupDayAsync"/>.
/// </para>
/// </summary>
public sealed class MongoAiUsageStore : IAiUsageStore
{
    private readonly IMongoDatabase _database;

    public MongoAiUsageStore(IMongoDatabase database)
    {
        _database = database;
    }

    private IMongoCollection<AiUsageEventDocument> Events =>
        _database.GetCollection<AiUsageEventDocument>(TelemetryCollections.AiUsageEvents);

    private IMongoCollection<AiUsageRollupDocument> Rollups =>
        _database.GetCollection<AiUsageRollupDocument>(TelemetryCollections.AiUsageRollups);

    public Task RecordAsync(AiUsageEventDocument usage, CancellationToken cancellationToken = default) =>
        Events.InsertOneAsync(usage, options: null, cancellationToken);

    /// <summary>
    /// Fold one day, then <b>replace</b> rather than increment.
    ///
    /// <para>
    /// Replacing is what makes a re-run safe. An <c>$inc</c>-based rollup double
    /// counts the moment it runs twice — which it will, because the obvious response
    /// to "yesterday looks wrong" is to run it again. Here the recomputed row is the
    /// whole truth for that day, so re-running is a no-op and a late event simply
    /// corrects the figure.
    /// </para>
    /// </summary>
    public async Task<int> RollupDayAsync(string day, CancellationToken cancellationToken = default)
    {
        // Element names are camelCase — MongoKernelConventions registers
        // CamelCaseElementNameConvention globally, so a pipeline written against the
        // C# property names matches nothing and every figure comes back zero.
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("day", day)),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = new BsonDocument { ["userId"] = "$userId", ["feature"] = "$feature" },
                ["month"] = new BsonDocument("$first", "$month"),
                ["calls"] = new BsonDocument("$sum", 1),
                ["errors"] = new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$outcome", AiUsageOutcome.Error }), 1, 0,
                    })),
                ["inputTokens"] = new BsonDocument("$sum", "$inputTokens"),
                ["outputTokens"] = new BsonDocument("$sum", "$outputTokens"),
                ["totalTokens"] = new BsonDocument("$sum", "$totalTokens"),
                ["cost"] = new BsonDocument("$sum", "$estimatedCostUsd"),
                ["unpriced"] = new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$priced", false }), 1, 0,
                    })),
                ["latency"] = new BsonDocument("$sum", "$latencyMs"),
            }),
        };

        var grouped = await Events
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A day whose events have all aged out of the TTL window would otherwise
        // silently keep its old rollup forever. Clearing first means "no events" and
        // "never rolled up" converge on the same visible answer: nothing.
        await Rollups
            .DeleteManyAsync(r => r.Day == day, cancellationToken)
            .ConfigureAwait(false);

        if (grouped.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var rows = grouped.Select(g =>
        {
            var key = g["_id"].AsBsonDocument;
            return new AiUsageRollupDocument
            {
                UserId = key["userId"].AsObjectId,
                Feature = key["feature"].AsString,
                Day = day,
                Month = g.GetValue("month", day.Length >= 7 ? day[..7] : day).AsString,
                Calls = g["calls"].ToInt32(),
                Errors = g["errors"].ToInt32(),
                InputTokens = g["inputTokens"].ToInt64(),
                OutputTokens = g["outputTokens"].ToInt64(),
                TotalTokens = g["totalTokens"].ToInt64(),
                EstimatedCostUsd = ToDecimal(g.GetValue("cost", BsonNull.Value)),
                UnpricedCalls = g["unpriced"].ToInt32(),
                TotalLatencyMs = g["latency"].ToInt64(),
                ComputedAt = now,
            };
        }).ToList();

        await Rollups.InsertManyAsync(rows, options: null, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<UsageTotals> TotalsAsync(UsageWindow window, CancellationToken cancellationToken = default)
    {
        var rows = await GroupAsync(WindowFilter(window), groupBy: null, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? UsageTotals.Zero : rows[0].Totals;
    }

    public Task<IReadOnlyList<UsageBucket>> ByFeatureAsync(
        UsageWindow window,
        CancellationToken cancellationToken = default) =>
        GroupAsync(WindowFilter(window), "$feature", cancellationToken);

    public async Task<IReadOnlyList<UsageBucket>> DailySeriesAsync(
        UsageWindow window,
        CancellationToken cancellationToken = default)
    {
        var rows = await GroupAsync(WindowFilter(window), "$day", cancellationToken).ConfigureAwait(false);
        return FillGaps(window, rows);
    }

    public async Task<IReadOnlyList<UserSpend>> TopSpendersAsync(
        UsageWindow window,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var all = await PerUserTotalsAsync(window, cancellationToken).ConfigureAwait(false);
        return all
            .OrderByDescending(u => u.Totals.EstimatedCostUsd)
            .ThenByDescending(u => u.Totals.TotalTokens)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public async Task<IReadOnlyList<UserSpend>> PerUserTotalsAsync(
        UsageWindow window,
        CancellationToken cancellationToken = default)
    {
        var rows = await GroupRawAsync(WindowFilter(window), "$userId", cancellationToken).ConfigureAwait(false);

        return rows
            .Where(r => r.GetValue("_id", BsonNull.Value).IsObjectId)
            .Select(r => new UserSpend(r["_id"].AsObjectId, ReadTotals(r)))
            .ToList();
    }

    public Task<IReadOnlyList<UsageBucket>> ForUserByFeatureAsync(
        ObjectId userId,
        UsageWindow window,
        CancellationToken cancellationToken = default) =>
        GroupAsync(WindowFilter(window) & Builders<AiUsageRollupDocument>.Filter.Eq(r => r.UserId, userId),
            "$feature",
            cancellationToken);

    public async Task<IReadOnlyList<UsageBucket>> ForUserDailyAsync(
        ObjectId userId,
        UsageWindow window,
        CancellationToken cancellationToken = default)
    {
        var rows = await GroupAsync(
                WindowFilter(window) & Builders<AiUsageRollupDocument>.Filter.Eq(r => r.UserId, userId),
                "$day",
                cancellationToken)
            .ConfigureAwait(false);

        return FillGaps(window, rows);
    }

    public async Task<IReadOnlyList<ErrorBucket>> ByErrorAsync(
        UsageWindow window,
        CancellationToken cancellationToken = default)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["day"] = new BsonDocument { ["$gte"] = window.FromDay, ["$lte"] = window.ToDay },
                ["outcome"] = AiUsageOutcome.Error,
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = new BsonDocument
                {
                    ["feature"] = "$feature",

                    // `IgnoreIfNull` means a null errorCode is absent, not stored —
                    // so this coalesces to a named bucket rather than grouping every
                    // uncoded failure under a missing key.
                    ["errorCode"] = new BsonDocument("$ifNull", new BsonArray { "$errorCode", "unknown" }),
                },
                ["count"] = new BsonDocument("$sum", 1),
                ["lastSeen"] = new BsonDocument("$max", "$at"),
            }),
            new BsonDocument("$sort", new BsonDocument("count", -1)),
            new BsonDocument("$limit", 50),
        };

        var rows = await Events
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r =>
            {
                var key = r["_id"].AsBsonDocument;
                return new ErrorBucket(
                    key.GetValue("feature", "unknown").AsString,
                    key.GetValue("errorCode", "unknown").AsString,
                    r.GetValue("count", 0).ToInt32(),
                    r.GetValue("lastSeen", BsonNull.Value) is var seen && seen.IsValidDateTime
                        ? seen.ToUniversalTime()
                        : DateTime.MinValue);
            })
            .ToList();
    }

    // ---- shared aggregation ------------------------------------------------

    private static FilterDefinition<AiUsageRollupDocument> WindowFilter(UsageWindow window)
    {
        var builder = Builders<AiUsageRollupDocument>.Filter;

        // String comparison is correct here precisely because the key is
        // zero-padded ISO — "2026-08-09" < "2026-08-17" lexically and calendrically.
        return builder.Gte(r => r.Day, window.FromDay) & builder.Lte(r => r.Day, window.ToDay);
    }

    private async Task<IReadOnlyList<UsageBucket>> GroupAsync(
        FilterDefinition<AiUsageRollupDocument> filter,
        string? groupBy,
        CancellationToken cancellationToken)
    {
        var rows = await GroupRawAsync(filter, groupBy, cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => new UsageBucket(
                r.GetValue("_id", BsonNull.Value) is var id && id.IsBsonNull ? string.Empty : id.ToString()!,
                ReadTotals(r)))
            .OrderBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<BsonDocument>> GroupRawAsync(
        FilterDefinition<AiUsageRollupDocument> filter,
        string? groupBy,
        CancellationToken cancellationToken)
    {
        var rendered = filter.Render(new RenderArgs<AiUsageRollupDocument>(
            Rollups.DocumentSerializer,
            Rollups.Settings.SerializerRegistry));

        var pipeline = new[]
        {
            new BsonDocument("$match", rendered),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = groupBy is null ? BsonNull.Value : groupBy,
                ["calls"] = new BsonDocument("$sum", "$calls"),
                ["errors"] = new BsonDocument("$sum", "$errors"),
                ["inputTokens"] = new BsonDocument("$sum", "$inputTokens"),
                ["outputTokens"] = new BsonDocument("$sum", "$outputTokens"),
                ["totalTokens"] = new BsonDocument("$sum", "$totalTokens"),
                ["cost"] = new BsonDocument("$sum", "$estimatedCostUsd"),
                ["unpriced"] = new BsonDocument("$sum", "$unpricedCalls"),
            }),
        };

        return await Rollups
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static UsageTotals ReadTotals(BsonDocument row) => new(
        row.GetValue("calls", 0).ToInt32(),
        row.GetValue("errors", 0).ToInt32(),
        row.GetValue("inputTokens", 0L).ToInt64(),
        row.GetValue("outputTokens", 0L).ToInt64(),
        row.GetValue("totalTokens", 0L).ToInt64(),
        ToDecimal(row.GetValue("cost", BsonNull.Value)),
        row.GetValue("unpriced", 0).ToInt32());

    /// <summary>
    /// <c>$sum</c> over an empty set yields <c>0</c> as an int32, not a Decimal128, so
    /// this cannot simply be <c>AsDecimal</c>.
    /// </summary>
    private static decimal ToDecimal(BsonValue value) => value.BsonType switch
    {
        BsonType.Decimal128 => (decimal)value.AsDecimal128,
        BsonType.Double => (decimal)value.AsDouble,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        _ => 0m,
    };

    /// <summary>
    /// Every day in the window gets a row. A missing day is a real zero — the product
    /// was not used — and a chart that omits it draws a straight line across the gap,
    /// which reads as steady usage rather than as silence.
    /// </summary>
    private static IReadOnlyList<UsageBucket> FillGaps(UsageWindow window, IReadOnlyList<UsageBucket> rows)
    {
        if (!TryParseDay(window.FromDay, out var from) || !TryParseDay(window.ToDay, out var to) || to < from)
        {
            return rows;
        }

        var found = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var filled = new List<UsageBucket>();

        for (var cursor = from; cursor <= to; cursor = cursor.AddDays(1))
        {
            var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            filled.Add(found.TryGetValue(key, out var row) ? row : new UsageBucket(key, UsageTotals.Zero));
        }

        return filled;
    }

    private static bool TryParseDay(string day, out DateTime parsed) =>
        DateTime.TryParseExact(
            day,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out parsed);
}
