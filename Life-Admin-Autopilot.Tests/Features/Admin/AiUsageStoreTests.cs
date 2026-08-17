using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// Mongo-backed checks for the usage store.
///
/// <para>
/// <b>These exist because the failure they guard is invisible to a unit test.</b>
/// <c>MongoKernelConventions</c> registers <c>CamelCaseElementNameConvention</c>
/// globally, so an aggregation pipeline written against the C# property names
/// (<c>$Day</c>, <c>$UserId</c>) matches nothing and every figure comes back as a
/// confident zero. No mock reproduces that — only a real round trip does. The first
/// draft of <c>MongoAiUsageStore</c> had exactly this bug.
/// </para>
///
/// <para>
/// Own database, never <c>kitto_dev</c>: the dev database is shared with a running
/// stack, and a test that truncated its collections would be a genuinely bad day.
/// Skips silently when nothing is listening, matching the convention the ICS and
/// task repository tests already use.
/// </para>
/// </summary>
public class AiUsageStoreTests
{
    private const string TestDatabase = "kitto_admin_usage_tests";

    [Fact]
    public async Task Rolls_raw_events_up_and_reads_them_back()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var store = new MongoAiUsageStore(db);
        var (alice, bob) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        const string day = "2026-08-17";

        await ResetAsync(db);

        await store.RecordAsync(Event(alice, day, AiUsageFeature.Chat, 29_395, 643, 0.010426m));
        await store.RecordAsync(Event(alice, day, AiUsageFeature.Chat, 30_000, 700, 0.010750m));
        await store.RecordAsync(Event(bob, day, AiUsageFeature.DocumentScan, 5_000, 200, 0.002000m));

        var rows = await store.RollupDayAsync(day);
        Assert.Equal(2, rows); // {alice, chat} and {bob, document_scan}

        var window = UsageWindow.Day(day);
        var totals = await store.TotalsAsync(window);

        // The assertion that would have caught the camelCase bug: a wrong field name
        // yields 0 calls here, not an error.
        Assert.Equal(3, totals.Calls);
        Assert.Equal(64_395, totals.InputTokens);
        Assert.Equal(1_543, totals.OutputTokens);
        Assert.Equal(0.023176m, totals.EstimatedCostUsd);

        var byFeature = await store.ByFeatureAsync(window);
        Assert.Equal(2, byFeature.Count);
        Assert.Equal(2, byFeature.Single(b => b.Key == AiUsageFeature.Chat).Totals.Calls);

        var perUser = await store.PerUserTotalsAsync(window);
        Assert.Equal(0.021176m, perUser.Single(u => u.UserId == alice).Totals.EstimatedCostUsd);

        var top = await store.TopSpendersAsync(window, 10);
        Assert.Equal(alice, top[0].UserId);
    }

    /// <summary>
    /// Re-running a day must be a no-op, because the obvious response to "yesterday
    /// looks wrong" is to run it again — and an <c>$inc</c>-based rollup would double
    /// every figure when someone did.
    /// </summary>
    [Fact]
    public async Task Rolling_the_same_day_twice_does_not_double_count()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var store = new MongoAiUsageStore(db);
        var user = ObjectId.GenerateNewId();
        const string day = "2026-08-16";

        await ResetAsync(db);
        await store.RecordAsync(Event(user, day, AiUsageFeature.Chat, 1_000, 100, 0.001m));

        await store.RollupDayAsync(day);
        await store.RollupDayAsync(day);
        await store.RollupDayAsync(day);

        var totals = await store.TotalsAsync(UsageWindow.Day(day));
        Assert.Equal(1, totals.Calls);
        Assert.Equal(1_000, totals.InputTokens);
    }

    /// <summary>
    /// A day nobody used the product must come back as a zero row, not be absent — a
    /// line chart that skips missing days draws straight through them and reads as
    /// steady usage.
    /// </summary>
    [Fact]
    public async Task Daily_series_fills_gaps_with_zeroes()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var store = new MongoAiUsageStore(db);
        var user = ObjectId.GenerateNewId();

        await ResetAsync(db);
        await store.RecordAsync(Event(user, "2026-08-10", AiUsageFeature.Chat, 100, 10, 0.001m));
        await store.RecordAsync(Event(user, "2026-08-13", AiUsageFeature.Chat, 100, 10, 0.001m));
        await store.RollupDayAsync("2026-08-10");
        await store.RollupDayAsync("2026-08-13");

        var series = await store.DailySeriesAsync(new UsageWindow("2026-08-10", "2026-08-13"));

        Assert.Equal(4, series.Count);
        Assert.Equal(new[] { "2026-08-10", "2026-08-11", "2026-08-12", "2026-08-13" }, series.Select(s => s.Key));
        Assert.Equal(1, series[0].Totals.Calls);
        Assert.Equal(0, series[1].Totals.Calls);
        Assert.Equal(0, series[2].Totals.Calls);
        Assert.Equal(1, series[3].Totals.Calls);
    }

    /// <summary>
    /// An unpriced call contributes real tokens and zero dollars. The count has to
    /// survive the rollup, or the console cannot caveat the total it shows.
    /// </summary>
    [Fact]
    public async Task Unpriced_calls_are_counted_through_the_rollup()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var store = new MongoAiUsageStore(db);
        var user = ObjectId.GenerateNewId();
        const string day = "2026-08-15";

        await ResetAsync(db);

        var unpriced = Event(user, day, AiUsageFeature.Chat, 1_000, 100, 0m);
        unpriced.Priced = false;
        await store.RecordAsync(unpriced);
        await store.RecordAsync(Event(user, day, AiUsageFeature.Chat, 1_000, 100, 0.001m));

        await store.RollupDayAsync(day);
        var totals = await store.TotalsAsync(UsageWindow.Day(day));

        Assert.Equal(2, totals.Calls);
        Assert.Equal(1, totals.UnpricedCalls);
        Assert.Equal(0.001m, totals.EstimatedCostUsd);
    }

    /// <summary>
    /// A day whose events aged out of the TTL window must not keep its old rollup
    /// forever — "no events" and "never rolled up" have to look the same.
    /// </summary>
    [Fact]
    public async Task Rolling_a_day_with_no_events_clears_a_stale_rollup()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var store = new MongoAiUsageStore(db);
        var user = ObjectId.GenerateNewId();
        const string day = "2026-08-14";

        await ResetAsync(db);
        await store.RecordAsync(Event(user, day, AiUsageFeature.Chat, 100, 10, 0.001m));
        await store.RollupDayAsync(day);
        Assert.Equal(1, (await store.TotalsAsync(UsageWindow.Day(day))).Calls);

        await Events(db).DeleteManyAsync(Builders<AiUsageEventDocument>.Filter.Empty);
        await store.RollupDayAsync(day);

        Assert.Equal(0, (await store.TotalsAsync(UsageWindow.Day(day))).Calls);
    }

    // ---- harness -----------------------------------------------------------

    private static AiUsageEventDocument Event(
        ObjectId userId,
        string day,
        string feature,
        int input,
        int output,
        decimal cost) => new()
    {
        UserId = userId,
        At = DateTime.UtcNow,
        Day = day,
        Month = day[..7],
        Feature = feature,
        Provider = "langflow",
        Model = "gemini-2.5-flash",
        InputTokens = input,
        OutputTokens = output,
        TotalTokens = input + output,
        EstimatedCostUsd = cost,
        Priced = true,
        LatencyMs = 1_200,
        Outcome = AiUsageOutcome.Ok,
        ExpiresAt = DateTime.UtcNow.AddDays(90),
    };

    private static IMongoCollection<AiUsageEventDocument> Events(IMongoDatabase db) =>
        db.GetCollection<AiUsageEventDocument>(TelemetryCollections.AiUsageEvents);

    private static async Task ResetAsync(IMongoDatabase db)
    {
        await Events(db).DeleteManyAsync(Builders<AiUsageEventDocument>.Filter.Empty);
        await db.GetCollection<AiUsageRollupDocument>(TelemetryCollections.AiUsageRollups)
            .DeleteManyAsync(Builders<AiUsageRollupDocument>.Filter.Empty);
    }

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(TestDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
