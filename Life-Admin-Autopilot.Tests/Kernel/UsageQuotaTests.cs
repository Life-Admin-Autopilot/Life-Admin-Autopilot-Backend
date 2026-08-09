using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The single quota primitive, against the parity Mongo. Skipped (not failed)
/// when that instance is not running, so the suite stays green on a machine
/// without it.
/// </summary>
public sealed class UsageQuotaTests
{
    private const string TestCollection = "kerneltestusagecounters";

    [Fact]
    public async Task admits_up_to_the_limit_then_denies_with_the_live_count()
    {
        // Arrange
        var store = TryCreateStore(out var database);
        if (store is null)
        {
            return;
        }

        var bucket = FreshBucket(limit: 2);
        await Reset(database!, bucket);

        // Act + Assert — first two admitted, third refused.
        Assert.True((await store.TryAdmitAsync(bucket)).Admitted);
        Assert.True((await store.TryAdmitAsync(bucket)).Admitted);

        var denied = await store.TryAdmitAsync(bucket);
        Assert.False(denied.Admitted);

        // The refusal reports the LIVE count so the upgrade prompt is honest.
        Assert.Equal(2, denied.Used);
        Assert.Equal(2, denied.Limit);
    }

    [Fact]
    public async Task inserts_the_first_row_at_one_not_at_the_limit()
    {
        // Arrange — the subtle bit: the guard is a RANGE predicate, so the upsert
        // seeds only the equality fields plus the $inc.
        var store = TryCreateStore(out var database);
        if (store is null)
        {
            return;
        }

        var bucket = FreshBucket(limit: 5);
        await Reset(database!, bucket);

        // Act
        await store.TryAdmitAsync(bucket);

        // Assert
        Assert.Equal(1, await store.ReadUsedAsync(bucket));
    }

    [Fact]
    public async Task release_hands_a_slot_back_and_never_goes_negative()
    {
        // Arrange
        var store = TryCreateStore(out var database);
        if (store is null)
        {
            return;
        }

        var bucket = FreshBucket(limit: 1);
        await Reset(database!, bucket);

        // Act
        await store.TryAdmitAsync(bucket);
        await store.ReleaseAsync(bucket);
        await store.ReleaseAsync(bucket);

        // Assert — the second release is a no-op, not a -1.
        Assert.Equal(0, await store.ReadUsedAsync(bucket));

        // And the freed slot is genuinely reusable.
        Assert.True((await store.TryAdmitAsync(bucket)).Admitted);
    }

    [Fact]
    public async Task a_zero_limit_never_admits_and_writes_nothing()
    {
        // Arrange
        var store = TryCreateStore(out var database);
        if (store is null)
        {
            return;
        }

        var bucket = FreshBucket(limit: 0);
        await Reset(database!, bucket);

        // Act
        var admission = await store.TryAdmitAsync(bucket);

        // Assert — an upsert here would insert a row at 1 above a limit of 0.
        Assert.False(admission.Admitted);
        Assert.Equal(0, await store.ReadUsedAsync(bucket));
    }

    [Fact]
    public async Task record_increments_past_the_limit_because_it_is_not_gated()
    {
        // Arrange — Node's recordUsage: count the post-confirmation continuation of a
        // turn the user already paid for, but never refuse it.
        var store = TryCreateStore(out var database);
        if (store is null)
        {
            return;
        }

        var bucket = FreshBucket(limit: 1);
        await Reset(database!, bucket);

        // Act
        await store.TryAdmitAsync(bucket);
        await store.RecordAsync(bucket);

        // Assert
        Assert.Equal(2, await store.ReadUsedAsync(bucket));
    }

    [Fact]
    public void bucket_keys_match_the_node_formats()
    {
        // Arrange
        var at = new DateTime(2026, 8, 9, 18, 0, 0, DateTimeKind.Utc);

        // Assert
        Assert.Equal("2026-08-09", UsageQuotaBuckets.UtcDate(at));
        Assert.Equal("2026-08", UsageQuotaBuckets.UtcMonth(at));
        Assert.Equal("2026-08-10T00:00:00.000Z", UsageQuotaBuckets.NextUtcMidnightIso(at));
        Assert.Equal("2026-09-01T00:00:00.000Z", UsageQuotaBuckets.NextMonthStartIso("2026-08"));
    }

    [Fact]
    public void december_rolls_into_january()
    {
        // Assert — Node passes the 1-based month into the 0-indexed Date.UTC, which
        // overflows into the next year. Deliberate there, reproduced here.
        Assert.Equal("2027-01-01T00:00:00.000Z", UsageQuotaBuckets.NextMonthStartIso("2026-12"));
    }

    private static UsageQuotaBucket FreshBucket(int limit) => new(
        TestCollection,
        ObjectId.GenerateNewId(),
        new Dictionary<string, string> { ["date"] = "2026-08-09", ["kind"] = "message" },
        limit);

    /// <summary>
    /// Clears the bucket AND creates the unique index the primitive depends on —
    /// the same thing <c>KernelIndexProvider</c> does for the three real counters.
    /// A slice introducing its own counter collection must do likewise.
    /// </summary>
    private static async Task Reset(IMongoDatabase database, UsageQuotaBucket bucket)
    {
        await database.DropCollectionAsync(TestCollection);
        await KernelIndexProvider.EnsureQuotaIndexAsync(database, bucket);
    }

    private static IUsageQuotaStore? TryCreateStore(out IMongoDatabase? database)
    {
        database = null;
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var db = new MongoClient(settings).GetDatabase(KernelWebApplicationFactory.ParityDatabase);
            db.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            database = db;
            return new MongoUsageQuotaStore(db);
        }
        catch (Exception)
        {
            // Parity Mongo is not running — the rest of the suite still has value.
            return null;
        }
    }
}
