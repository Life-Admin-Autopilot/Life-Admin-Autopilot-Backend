using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot_Backend.Kernel.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The daily AI message quota, on the kernel's single primitive. Mongo-backed
/// cases skip when the parity instance is not running.
/// </summary>
public sealed class AiQuotaServiceTests
{
    [Fact]
    public void resolves_every_user_to_the_free_tier()
    {
        // resolveTier() is a v1 CONSTANT — Pro comes later. Reading
        // user.subscription.tier here would diverge the day anyone sets it.
        Assert.Equal("free", AiQuotaService.ResolveTier());
    }

    [Fact]
    public void defaults_to_thirty_free_and_three_hundred_pro_messages_a_day()
    {
        var service = Service();

        Assert.Equal(30, service.LimitFor("free", "message"));
        Assert.Equal(300, service.LimitFor("pro", "message"));

        // quotaFor() returns 0 for anything that is not `message`.
        Assert.Equal(0, service.LimitFor("free", "voice"));
    }

    [Theory]
    [InlineData("50", 50)]
    [InlineData("0", 30)]      // z.coerce.number().positive() — a non-positive value never applies
    [InlineData("-5", 30)]
    [InlineData("nonsense", 30)]
    [InlineData(null, 30)]
    public void reads_the_daily_ceiling_from_configuration(string? configured, int expected)
    {
        Assert.Equal(expected, Service(configured).LimitFor("free", "message"));
    }

    [Fact]
    public void builds_the_402_with_the_ai_specific_details_shape()
    {
        // A DIFFERENT shape from the document-scan 402's {tier,limit,used}: this one
        // adds `kind` and `resetAt`. And the message pluralises the KIND, so it
        // reads "30 messages".
        var at = new DateTime(2026, 8, 10, 13, 45, 0, DateTimeKind.Utc);

        var error = AiQuotaService.QuotaExceeded("free", "message", 30, 30, at);

        Assert.Equal(402, error.Status);
        Assert.Equal("quota_exceeded", error.Code);
        Assert.Equal("You've hit today's limit of 30 messages.", error.Message);

        var details = JsonSerializer.Serialize(error.Details, KernelJson.Lenient);
        Assert.Equal(
            """{"kind":"message","tier":"free","limit":30,"used":30,"resetAt":"2026-08-11T00:00:00.000Z"}""",
            details);
    }

    [Fact]
    public async Task reports_the_meter_with_the_next_utc_midnight_reset()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var service = Service(database: database);
        var userId = await FreshUserAsync(database);

        var before = await service.GetStatusAsync(userId, "free");
        var meter = Assert.Single(before);

        Assert.Equal("message", meter.Kind);
        Assert.Equal(30, meter.Limit);
        Assert.Equal(0, meter.Used);
        Assert.Equal(30, meter.Remaining);
        Assert.Equal(UsageQuotaBuckets.NextUtcMidnightIso(), meter.ResetAt);
        Assert.EndsWith("T00:00:00.000Z", meter.ResetAt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task admit_consumes_a_slot_and_release_hands_it_back()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var service = Service(database: database);
        var userId = await FreshUserAsync(database);

        await service.AdmitAsync(userId, "free");
        await service.AdmitAsync(userId, "free");
        Assert.Equal(2, (await service.GetStatusAsync(userId, "free")).Single().Used);

        // Refund exactly one — the contract is one release per admit, and never on
        // undo (that would make do -> undo -> do an unlimited loop around the cap).
        await service.ReleaseAsync(userId);
        var after = (await service.GetStatusAsync(userId, "free")).Single();
        Assert.Equal(1, after.Used);
        Assert.Equal(29, after.Remaining);
    }

    [Fact]
    public async Task record_increments_without_being_gated()
    {
        // recordUsage() counts the post-confirmation continuation, which is the same
        // logical turn the user already paid for: visible in the meter, never
        // refused.
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var service = Service("1", database);
        var userId = await FreshUserAsync(database);

        await service.AdmitAsync(userId, "free");
        await service.RecordAsync(userId);
        await service.RecordAsync(userId);

        var meter = (await service.GetStatusAsync(userId, "free")).Single();
        Assert.Equal(3, meter.Used);

        // Over the ceiling, so remaining floors at zero rather than going negative.
        Assert.Equal(0, meter.Remaining);
    }

    [Fact]
    public async Task refuses_once_the_day_is_spent_and_reports_the_live_count()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var service = Service("2", database);
        var userId = await FreshUserAsync(database);

        await service.AdmitAsync(userId, "free");
        await service.AdmitAsync(userId, "free");

        var error = await Assert.ThrowsAsync<AppException>(() => service.AdmitAsync(userId, "free"));

        Assert.Equal(402, error.Status);
        Assert.Equal("quota_exceeded", error.Code);
        Assert.Equal("You've hit today's limit of 2 messages.", error.Message);

        var details = JsonSerializer.Serialize(error.Details, KernelJson.Lenient);
        Assert.Contains("\"used\":2", details, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"message\"", details, StringComparison.Ordinal);
        Assert.Contains("\"tier\":\"free\"", details, StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------

    private static AiQuotaService Service(string? freeDaily = null, IMongoDatabase? database = null)
    {
        var settings = new Dictionary<string, string?>();
        if (freeDaily is not null)
        {
            settings["AI_QUOTA_FREE_DAILY"] = freeDaily;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // The store is only touched by the Mongo-backed cases; the pure ones never
        // reach it, so a null database is fine there.
        return new AiQuotaService(new MongoUsageQuotaStore(database!), configuration);
    }

    private static async Task<ObjectId> FreshUserAsync(IMongoDatabase database)
    {
        var userId = ObjectId.GenerateNewId();
        await database
            .GetCollection<BsonDocument>(MongoCollections.AiUsageCounters)
            .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));

        return userId;
    }
}
