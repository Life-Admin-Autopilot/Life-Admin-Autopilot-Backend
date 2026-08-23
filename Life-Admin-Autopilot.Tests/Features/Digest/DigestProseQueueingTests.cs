using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Digest;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.DAL.Features.Digest;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Digest;

/// <summary>
/// A digest server that believes it can reach a model. The key is a placeholder and
/// is never called — background workers are off under the test fixture, so a queued
/// job sits in the channel — which is exactly what makes <c>prosePending</c>
/// observable.
///
/// <para>
/// <b>The key is injected as a SERVICE, not as configuration.</b> Every slice that
/// wants <see cref="PlanningOptions"/> materialises it eagerly during
/// <c>AddServices</c>, and under minimal hosting that runs against
/// <c>builder.Configuration</c> before <c>WebApplicationFactory</c> layers its own
/// sources in — so <c>With("PLANNING_API_KEY", …)</c> reaches
/// <c>IConfiguration</c> and never reaches the options object. Replacing the
/// registration is the seam that actually works.
/// </para>
/// </summary>
public sealed class ProseEnabledDigestFactory : KernelWebApplicationFactory
{
    public const string ProseDatabase = "kitto_parity_dotnet_j_prose_tests";

    public ProseEnabledDigestFactory()
    {
        With("MongoDbSettings:DatabaseName", ProseDatabase);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<PlanningOptions>();
            services.AddSingleton(new PlanningOptions { ApiKey = "test-key-never-called" });
        });
    }
}

/// <summary>
/// WHEN the digest asks a model for a better headline.
///
/// <para>
/// The regression this exists for: prose was originally queued only on a cache MISS.
/// Almost every dashboard load is a cache HIT — the miss happens once, on the first
/// read after the user changes something — so a row written before a model was
/// reachable kept its computed count sentence for the rest of the day, and no amount
/// of reloading would ever produce the real one.
/// </para>
/// </summary>
public sealed class DigestProseQueueingTests : IClassFixture<ProseEnabledDigestFactory>
{
    private const string Tz = "Africa/Cairo";

    private readonly ProseEnabledDigestFactory _factory;

    public DigestProseQueueingTests(ProseEnabledDigestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task asks_for_a_sentence_on_the_first_read_of_a_day_with_matters()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedTaskAsync(db, userId, "Renew the car insurance", DueTodayInCairo());

        var digest = await GetDigestAsync(userId);

        Assert.True(digest.GetProperty("prosePending").GetBoolean());

        // The response does not WAIT for it — the counts and a plain headline ship now.
        Assert.Equal("1 matter due today.", digest.GetProperty("headline").GetString());
    }

    /// <summary>
    /// The bug, directly. A row whose fingerprint still matches is served from cache;
    /// if that path does not queue, a day that never got its sentence never will.
    /// </summary>
    [Fact]
    public async Task asks_again_on_a_cache_hit_that_has_never_been_through_a_model()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedTaskAsync(db, userId, "Chase the airline refund", DueTodayInCairo());

        // First read builds and caches the row, and queues the miss-path job.
        var first = await GetDigestAsync(userId);
        var localDate = first.GetProperty("localDate").GetString()!;

        // Retire that job. WITHOUT THIS THE TEST IS WORTHLESS: a pending job makes
        // `prosePending` true on its own, so the assertion below would hold even with
        // the cache-hit path removed — which is exactly the bug being guarded.
        var queue = _factory.Services.GetRequiredService<DigestProseQueue>();
        queue.Complete(await DrainOneAsync(queue));
        Assert.False(queue.IsPending(userId, localDate));

        // Now the state every row was in when this shipped: cached, fingerprint
        // current, never offered to a model.
        await Digests(db).UpdateOneAsync(
            Builders<DailyDigestDocument>.Filter.Eq(d => d.UserId, userId),
            Builders<DailyDigestDocument>.Update.Set(d => d.ProseAttemptedHash, null));

        var second = await GetDigestAsync(userId);

        Assert.Equal(localDate, second.GetProperty("localDate").GetString());
        Assert.True(second.GetProperty("prosePending").GetBoolean());
    }

    /// <summary>
    /// The other half of the same rule. Once a fingerprint has been through the model
    /// — even if it produced nothing and the plain headline stands — a hit must stop
    /// asking. The client refetches every couple of seconds while a job is pending, so
    /// "ask on every hit" is a request loop, not a retry.
    /// </summary>
    [Fact]
    public async Task stops_asking_once_this_state_has_had_its_attempt()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedTaskAsync(db, userId, "Order the new carrier", DueTodayInCairo());

        await GetDigestAsync(userId);

        // Run the job the way the worker would, for the case that matters: the model
        // answered nothing. The write-back stamps the attempt and leaves the computed
        // headline alone, and the queue is told the job is over either way.
        var queue = _factory.Services.GetRequiredService<DigestProseQueue>();
        var job = await DrainOneAsync(queue);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<DailyDigestRepository>()
                .CompleteProseAsync(job.UserId, job.LocalDate, job.SourceHash, null, DateTime.UtcNow);
        }

        queue.Complete(job);

        var second = await GetDigestAsync(userId);

        Assert.False(second.GetProperty("prosePending").GetBoolean());
        Assert.Equal("1 matter due today.", second.GetProperty("headline").GetString());
    }

    /// <summary>
    /// And the success case: a sentence written back under the current fingerprint is
    /// what the next read serves, in place of the count.
    /// </summary>
    [Fact]
    public async Task serves_the_models_sentence_once_it_has_been_written_back()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        const string Sentence = "Today you're renewing the car insurance and booking the tickets.";

        var userId = ObjectId.GenerateNewId();
        await SeedTaskAsync(db, userId, "Renew the car insurance", DueTodayInCairo());

        await GetDigestAsync(userId);

        var queue = _factory.Services.GetRequiredService<DigestProseQueue>();
        var job = await DrainOneAsync(queue);

        // The pool really does carry what a sentence would be built from — an id-only
        // pool would leave the model nothing to name.
        Assert.Contains(job.Pool, m => m.Title == "Renew the car insurance");

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<DailyDigestRepository>()
                .CompleteProseAsync(job.UserId, job.LocalDate, job.SourceHash, Sentence, DateTime.UtcNow);
        }

        queue.Complete(job);

        var second = await GetDigestAsync(userId);

        Assert.Equal(Sentence, second.GetProperty("headline").GetString());
        Assert.False(second.GetProperty("prosePending").GetBoolean());

        // The model moved the sentence and nothing else.
        Assert.Equal(1, second.GetProperty("counts").GetProperty("dueToday").GetInt32());
    }

    [Fact]
    public async Task says_nothing_is_coming_for_a_day_with_no_matters_to_describe()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        // A model cannot write "today you are doing X" out of an empty pool, and a
        // client told to wait for a sentence nobody can write polls forever.
        var digest = await GetDigestAsync(ObjectId.GenerateNewId());

        Assert.False(digest.GetProperty("prosePending").GetBoolean());
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<JsonElement> GetDigestAsync(ObjectId userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/me/digest?tz={Tz}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(userId.ToString(), $"{userId}@example.test"));

        var response = await _factory.CreateApiClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return json.GetProperty("digest");
    }

    /// <summary>Take the next queued job, exactly as the worker's loop would.</summary>
    private static async Task<DigestProseJob> DrainOneAsync(DigestProseQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var job in queue.ReadAllAsync(cts.Token))
        {
            return job;
        }

        throw new InvalidOperationException("nothing was queued");
    }

    /// <summary>
    /// A due time that lands inside TODAY in Cairo whatever the wall clock says when
    /// the suite runs.
    ///
    /// <para>
    /// This used to be <c>DateTime.UtcNow.AddHours(3)</c>, and that is a clock bomb.
    /// Cairo is UTC+3, so it resolves to Cairo-now plus three hours — and from 21:00
    /// Cairo onward it crosses midnight and the matter becomes due TOMORROW.
    /// <c>dueToday</c> counts <c>[TodayStart, TomorrowStart)</c> in the user's zone,
    /// so the count fell to zero, the headline flipped to "Nothing due today.", and
    /// every test in this class that seeds a matter failed — for the three hours
    /// between 18:00 and 21:00 UTC, every day, on code nobody had touched.
    /// </para>
    ///
    /// <para>
    /// Midday is safe from both ends of the day: still ahead at 00:05, still today at
    /// 23:50. Whether the matter is past or future does not matter here — the facet
    /// counts by local day, not by remaining time.
    /// </para>
    /// </summary>
    private static DateTime DueTodayInCairo()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Tz);
        var middayToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date.AddHours(12);
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(middayToday, DateTimeKind.Unspecified),
            zone);
    }

    private static async Task SeedTaskAsync(
        IMongoDatabase database,
        ObjectId userId,
        string title,
        DateTime dueAt)
    {
        var now = DateTime.UtcNow;

        await database.GetCollection<BsonDocument>(MongoCollections.Tasks).InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["title"] = title,
            ["domain"] = "home",
            ["kind"] = "list",
            ["status"] = "open",
            ["priority"] = "normal",
            ["dueAt"] = dueAt,
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(),
            ["rescheduleCount"] = 0,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        });
    }

    private static IMongoCollection<DailyDigestDocument> Digests(IMongoDatabase database) =>
        database.GetCollection<DailyDigestDocument>(DigestCollections.DailyDigests);

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(ProseEnabledDigestFactory.ProseDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
