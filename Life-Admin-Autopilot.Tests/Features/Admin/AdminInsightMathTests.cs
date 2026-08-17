using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// The arithmetic behind every number the console shows.
///
/// <para>
/// These run against the real store, because the failure mode they guard is a
/// pipeline that returns a confident zero rather than an error — the exact class
/// of bug that unit tests with mocks cannot see.
/// </para>
/// </summary>
[Collection("admin-serial")]
public sealed class AdminInsightMathTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminInsightMathTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Today, as the store's own bucket key. Everything is seeded relative to this.</summary>
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private static string DaysAgo(int n) => DateTime.UtcNow.AddDays(-n).ToString("yyyy-MM-dd");

    private async Task<IMongoDatabase> FreshAsync()
    {
        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "users", "aiusageevents", "aiusagerollups");
        return db;
    }

    private static BsonDocument Rollup(
        ObjectId userId,
        string day,
        string feature,
        int calls,
        long input,
        long output,
        decimal cost,
        int errors = 0,
        int unpriced = 0) => new()
    {
        ["userId"] = userId,
        ["day"] = day,
        ["month"] = day[..7],
        ["feature"] = feature,
        ["calls"] = calls,
        ["errors"] = errors,
        ["inputTokens"] = input,
        ["outputTokens"] = output,
        ["totalTokens"] = input + output,
        ["estimatedCostUsd"] = new BsonDecimal128(cost),
        ["unpricedCalls"] = unpriced,
        ["totalLatencyMs"] = calls * 4000L,
        ["computedAt"] = DateTime.UtcNow,
    };

    // ---- totals ------------------------------------------------------------

    /// <summary>
    /// Sums across users, days and features. A camelCase slip in the aggregation
    /// pipeline makes every one of these zero — silently.
    /// </summary>
    [Fact]
    public async Task pulse_sums_across_users_days_and_features()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();
        var alice = ObjectId.GenerateNewId();
        var bob = ObjectId.GenerateNewId();
        var rollups = db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups);

        await rollups.InsertManyAsync(new[]
        {
            Rollup(alice, Today, AiUsageFeature.Chat, 4, 29_395 * 4, 643 * 4, 0.041704m),
            Rollup(bob, Today, AiUsageFeature.Chat, 2, 10_000, 500, 0.004250m),
            Rollup(bob, Today, AiUsageFeature.DocumentScan, 1, 8_000, 900, 0.004650m),
            Rollup(alice, DaysAgo(3), AiUsageFeature.Chat, 3, 30_000, 600, 0.010500m),
        });

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/pulse?days=30"));

        var today = json.GetProperty("today");
        Assert.Equal(7, today.GetProperty("calls").GetInt32());
        Assert.Equal(135_580, today.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0.050604m, today.GetProperty("estimatedCostUsd").GetDecimal());

        // Active users counts distinct people with at least one call in the window.
        Assert.Equal(2, json.GetProperty("activeUsers").GetInt32());

        var byFeature = json.GetProperty("byFeature").EnumerateArray().ToList();
        Assert.Equal(2, byFeature.Count);

        var chat = byFeature.Single(f => f.GetProperty("key").GetString() == AiUsageFeature.Chat);
        Assert.Equal(9, chat.GetProperty("totals").GetProperty("calls").GetInt32());
    }

    /// <summary>
    /// A day with no usage comes back as a zero row rather than being absent — a
    /// line chart that skips missing days draws straight through them, which reads
    /// as steady usage rather than as silence.
    /// </summary>
    [Fact]
    public async Task the_daily_series_has_no_gaps()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();
        var user = ObjectId.GenerateNewId();

        await db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups).InsertManyAsync(new[]
        {
            Rollup(user, DaysAgo(6), AiUsageFeature.Chat, 1, 100, 10, 0.001m),
            Rollup(user, Today, AiUsageFeature.Chat, 1, 100, 10, 0.001m),
        });

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/daily?days=7"));

        var series = json.EnumerateArray().ToList();

        Assert.Equal(7, series.Count);
        Assert.Equal(2, series.Count(p => p.GetProperty("totals").GetProperty("calls").GetInt32() > 0));
        Assert.Equal(5, series.Count(p => p.GetProperty("totals").GetProperty("calls").GetInt32() == 0));

        // Ascending, so the chart draws left to right in time order.
        var days = series.Select(p => p.GetProperty("key").GetString()!).ToList();
        Assert.Equal(days.OrderBy(d => d, StringComparer.Ordinal), days);
    }

    /// <summary>
    /// An unpriced call contributes real tokens and zero dollars. The count has to
    /// survive to the console, or a total that under-reports is presented as fact.
    /// </summary>
    [Fact]
    public async Task unpriced_calls_are_reported_not_hidden()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();
        var user = ObjectId.GenerateNewId();

        await db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups).InsertOneAsync(
            Rollup(user, Today, AiUsageFeature.Chat, 5, 50_000, 1_000, 0.008m, unpriced: 3));

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/pulse?days=30"));

        Assert.Equal(3, json.GetProperty("today").GetProperty("unpricedCalls").GetInt32());
    }

    // ---- cost distribution -------------------------------------------------

    /// <summary>
    /// The break-even line is the entire point of the histogram: without it the
    /// distribution is trivia, and with it every user to the right is losing money.
    /// </summary>
    [Fact]
    public async Task the_cost_histogram_counts_users_above_break_even()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();
        var rollups = db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups);

        // Three cheap users and two expensive ones. Break-even defaults to $4.18.
        foreach (var cost in new[] { 0.05m, 0.20m, 1.50m })
        {
            await rollups.InsertOneAsync(
                Rollup(ObjectId.GenerateNewId(), Today, AiUsageFeature.Chat, 5, 1000, 100, cost));
        }

        foreach (var cost in new[] { 6.00m, 12.00m })
        {
            await rollups.InsertOneAsync(
                Rollup(ObjectId.GenerateNewId(), Today, AiUsageFeature.Chat, 400, 900_000, 20_000, cost));
        }

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/cost-distribution?days=30"));

        Assert.Equal(
            AdminInsightService.DefaultBreakEvenUsd,
            json.GetProperty("breakEvenUsd").GetDecimal());

        Assert.Equal(2, json.GetProperty("usersAboveBreakEven").GetInt32());

        // Median of {0.05, 0.20, 1.50, 6.00, 12.00} is 1.50 — the middle value, not
        // the mean. A mean here would be dragged to 3.95 by the two outliers and
        // would make the typical user look four times more expensive than they are.
        Assert.Equal(1.50m, json.GetProperty("medianUsd").GetDecimal());

        // Every user lands in exactly one bucket.
        var bucketed = json.GetProperty("buckets").EnumerateArray()
            .Sum(b => b.GetProperty("users").GetInt32());
        Assert.Equal(5, bucketed);
    }

    [Fact]
    public async Task the_cost_histogram_is_empty_rather_than_broken_with_no_data()
    {
        if (!_factory.MongoIsUp()) return;
        await FreshAsync();

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/cost-distribution?days=30"));

        Assert.Equal(0, json.GetProperty("usersAboveBreakEven").GetInt32());
        Assert.Equal(0m, json.GetProperty("medianUsd").GetDecimal());
        Assert.Equal(0m, json.GetProperty("meanUsd").GetDecimal());
        Assert.NotEmpty(json.GetProperty("buckets").EnumerateArray());
    }

    // ---- top spenders ------------------------------------------------------

    [Fact]
    public async Task top_spenders_are_ordered_by_cost_and_resolve_emails()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();
        var cheap = await AdminTestData.SeedUserAsync(db, "cheap@test.local");
        var pricey = await AdminTestData.SeedUserAsync(db, "pricey@test.local");
        var rollups = db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups);

        await rollups.InsertOneAsync(Rollup(cheap, Today, AiUsageFeature.Chat, 2, 1000, 100, 0.30m));
        await rollups.InsertOneAsync(Rollup(pricey, Today, AiUsageFeature.Chat, 90, 900_000, 9_000, 9.90m));

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/top-spenders?days=30&limit=10"));

        var rows = json.EnumerateArray().ToList();

        Assert.Equal("pricey@test.local", rows[0].GetProperty("email").GetString());
        Assert.Equal("cheap@test.local", rows[1].GetProperty("email").GetString());
    }

    /// <summary>
    /// A spender whose account was deleted still has rollup rows until erasure
    /// runs. Showing a placeholder keeps this page's total equal to Pulse's; silently
    /// dropping the row would make two screens disagree about the same window.
    /// </summary>
    [Fact]
    public async Task a_deleted_spender_is_shown_rather_than_dropped()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();

        await db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageRollups).InsertOneAsync(
            Rollup(ObjectId.GenerateNewId(), Today, AiUsageFeature.Chat, 10, 100_000, 2_000, 3.00m));

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/top-spenders?days=30&limit=10"));

        var row = Assert.Single(json.EnumerateArray().ToList());
        Assert.Equal("(deleted account)", row.GetProperty("email").GetString());
        Assert.Equal(3.00m, row.GetProperty("totals").GetProperty("estimatedCostUsd").GetDecimal());
    }

    // ---- reliability -------------------------------------------------------

    [Fact]
    public async Task errors_are_grouped_by_cause_most_frequent_first()
    {
        if (!_factory.MongoIsUp()) return;

        var db = await FreshAsync();
        var events = db.GetCollection<BsonDocument>(TelemetryCollections.AiUsageEvents);
        var user = ObjectId.GenerateNewId();

        BsonDocument Failure(string code) => new()
        {
            ["userId"] = user,
            ["at"] = DateTime.UtcNow,
            ["day"] = Today,
            ["month"] = Today[..7],
            ["feature"] = AiUsageFeature.Chat,
            ["provider"] = "langflow",
            ["inputTokens"] = 0,
            ["outputTokens"] = 0,
            ["totalTokens"] = 0,
            ["estimatedCostUsd"] = new BsonDecimal128(0m),
            ["priced"] = false,
            ["latencyMs"] = 1000,
            ["outcome"] = AiUsageOutcome.Error,
            ["errorCode"] = code,
            ["expiresAt"] = DateTime.UtcNow.AddDays(90),
        };

        await events.InsertManyAsync(new[]
        {
            Failure("usage_missing"), Failure("usage_missing"), Failure("usage_missing"),
            Failure("quota_exceeded"),
        });

        // A success must not appear in the reliability view.
        var ok = Failure("usage_missing");
        ok["outcome"] = AiUsageOutcome.Ok;
        ok.Remove("errorCode");
        await events.InsertOneAsync(ok);

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/errors?days=30"));

        var buckets = json.EnumerateArray().ToList();

        Assert.Equal(2, buckets.Count);
        Assert.Equal("usage_missing", buckets[0].GetProperty("errorCode").GetString());
        Assert.Equal(3, buckets[0].GetProperty("count").GetInt32());
        Assert.Equal(1, buckets[1].GetProperty("count").GetInt32());
    }
}
