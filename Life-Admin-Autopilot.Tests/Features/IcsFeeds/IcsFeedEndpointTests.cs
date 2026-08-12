using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>
/// Its own database, plus the two seams that make the network-facing half testable:
/// a scripted DNS resolver and a scripted transport for the <c>ics-feed</c> client.
/// </summary>
public sealed class IcsWebApplicationFactory : KernelWebApplicationFactory
{
    public const string IcsDatabase = "kitto_parity_dotnet_f_tests";

    public IcsWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", IcsDatabase);
    }

    /// <summary>Public by default, so a test only has to script the hosts it cares about.</summary>
    public StubDnsResolver Dns { get; } = new StubDnsResolver()
        .Resolving("feeds.example", "93.184.216.34")
        .Resolving("127.0.0.1", "127.0.0.1");

    public ScriptedHandler Transport { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IFeedDnsResolver>(Dns));

            services
                .AddHttpClient(FeedFetcherOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Transport);
        });
    }
}

/// <summary>
/// The four ICS routes end to end.
///
/// <para>
/// The Mongo-backed cases skip (rather than fail) when the parity instance is not
/// running, following the account slice. The auth cases need no database.
/// </para>
/// </summary>
public sealed class IcsFeedEndpointTests : IClassFixture<IcsWebApplicationFactory>
{
    /// <summary>
    /// One event, comfortably inside the sync window. The window is
    /// <c>[now-7d, now+365d]</c>, so a hard-coded far-future date would be silently
    /// filtered out and every reconcile assertion would read zero.
    /// </summary>
    private static readonly string Calendar =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1\r\n" +
        $"DTSTART:{Stamp(30)}\r\nSUMMARY:Parents evening\r\nLOCATION:The hall\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private readonly IcsWebApplicationFactory _factory;

    public IcsFeedEndpointTests(IcsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- auth --------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/me/ics-feeds")]
    [InlineData("POST", "/me/ics-feeds")]
    [InlineData("POST", "/me/ics-feeds/6a78c437aa461ae1dc64ffff/sync")]
    [InlineData("DELETE", "/me/ics-feeds/6a78c437aa461ae1dc64ffff")]
    public async Task rejects_a_missing_authorization_header(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- handler ORDER, which is observable --------------------------------

    [Fact]
    public async Task validates_the_body_before_looking_at_the_timezone()
    {
        // A user with no timezone AND a broken body gets invalid_feed, not
        // timezone_required.
        var response = await PostAsync(
            "/me/ics-feeds",
            ObjectId.GenerateNewId(),
            new { url = "", label = "L", domain = "nope" });

        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_feed", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task demands_a_timezone_BEFORE_vetting_the_url()
    {
        // THE ordering trap. The URL here is a loopback address the SSRF guard would
        // refuse outright — but the timezone check runs first, so the caller sees
        // timezone_required. Verified live; do not "fail on the worst problem first".
        var response = await PostAsync(
            "/me/ics-feeds",
            ObjectId.GenerateNewId(),
            new { url = "http://127.0.0.1:9/private.ics", label = "Loopback", domain = "family" });

        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("timezone_required", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Set your timezone before subscribing to a calendar — imported events have no timezone of their own.",
            json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task refuses_an_unsafe_url_once_a_timezone_exists()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);

        var response = await PostAsync(
            "/me/ics-feeds",
            userId,
            new { url = "http://127.0.0.1:9/private.ics", label = "Loopback", domain = "family" });

        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsafe_feed_url", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            FeedUrlGuard.NotPubliclyReachable,
            json.GetProperty("error").GetProperty("message").GetString());
    }

    // ---- subscribe ---------------------------------------------------------

    [Fact]
    public async Task subscribing_creates_the_row_syncs_inline_and_answers_201()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        _factory.Transport.Calendar(url, Calendar, etag: "\"v1\"");

        var response = await PostAsync("/me/ics-feeds", userId, new { url, label = "  Term dates  ", domain = "family" });
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var feed = json.GetProperty("feed");
        Assert.Equal(url, feed.GetProperty("url").GetString());
        Assert.Equal("Term dates", feed.GetProperty("label").GetString());
        Assert.Equal("active", feed.GetProperty("status").GetString());
        Assert.Equal(0, feed.GetProperty("failureCount").GetInt32());

        // toJSON strips the cache validators — they are plumbing, not client data.
        Assert.False(feed.TryGetProperty("etag", out _));
        Assert.False(feed.TryGetProperty("lastModified", out _));
        Assert.False(feed.TryGetProperty("_id", out _));

        // The sync ran INLINE, before responding.
        var sync = json.GetProperty("sync");
        Assert.Equal("synced", sync.GetProperty("status").GetString());
        Assert.Equal(1, sync.GetProperty("created").GetInt32());
        Assert.Equal(0, sync.GetProperty("needingConfirmation").GetInt32());

        // …and it really wrote a matter, namespaced under the feed id.
        var tasks = TryGetCollection<BsonDocument>(MongoCollections.Tasks)!;
        var matter = await tasks
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .FirstOrDefaultAsync();

        Assert.Equal("Parents evening", matter["title"].AsString);
        Assert.Equal("ics_feed", matter["externalSource"].AsString);
        Assert.Equal($"{feed.GetProperty("id").GetString()}::e1", matter["externalId"].AsString);
        Assert.Equal("reminder", matter["kind"].AsString);
        Assert.Equal("The hall", matter["notes"].AsString);
    }

    [Fact]
    public async Task re_subscribing_updates_in_place_and_answers_200()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        _factory.Transport.Calendar(url, Calendar);

        var first = await ReadJsonAsync(
            await PostAsync("/me/ics-feeds", userId, new { url, label = "Original", domain = "family" }));

        var second = await PostAsync("/me/ics-feeds", userId, new { url, label = "Renamed", domain = "car" });
        var json = await ReadJsonAsync(second);

        // 200, not 201 — a second row for the same URL would double every matter.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            first.GetProperty("feed").GetProperty("id").GetString(),
            json.GetProperty("feed").GetProperty("id").GetString());
        Assert.Equal("Renamed", json.GetProperty("feed").GetProperty("label").GetString());
        Assert.Equal("car", json.GetProperty("feed").GetProperty("domain").GetString());

        // Rule 1: the second poll upserts rather than blind-inserting.
        Assert.Equal(0, json.GetProperty("sync").GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task de_duplicates_on_the_NORMALISED_url()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var slug = $"{Guid.NewGuid():N}.ics";
        _factory.Transport.Calendar($"https://feeds.example/{slug}", Calendar);

        var created = await PostAsync(
            "/me/ics-feeds", userId, new { url = $"https://feeds.example/{slug}", label = "A", domain = "family" });

        // webcal:, an uppercase host and the default port all normalise to the same
        // stored value, so this must UPDATE rather than create a second subscription.
        var again = await PostAsync(
            "/me/ics-feeds", userId, new { url = $"WEBCAL://FEEDS.EXAMPLE:443/{slug}", label = "B", domain = "family" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task reports_an_unhealthy_feed_in_the_body_with_a_2xx()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        _factory.Transport.Status(url, HttpStatusCode.NotFound);

        var response = await PostAsync("/me/ics-feeds", userId, new { url, label = "Dead", domain = "family" });
        var json = await ReadJsonAsync(response);

        // The SUBSCRIPTION succeeded; the publisher is what is broken.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("gone", json.GetProperty("feed").GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("feed").GetProperty("failureCount").GetInt32());
        Assert.Equal("That feed no longer exists.", json.GetProperty("feed").GetProperty("lastError").GetString());
        Assert.Equal("gone", json.GetProperty("sync").GetProperty("status").GetString());
        Assert.Equal("That feed no longer exists.", json.GetProperty("sync").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task files_an_ambiguous_time_as_a_passive_list_item()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";

        // A floating DTSTART: no Z, no TZID. Low confidence, so it must NOT fire.
        _factory.Transport.Calendar(
            url,
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:f1\r\n" +
            $"DTSTART:{Stamp(30).TrimEnd('Z')}\r\nSUMMARY:Club\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n");

        var json = await ReadJsonAsync(
            await PostAsync("/me/ics-feeds", userId, new { url, label = "Club", domain = "family" }));

        Assert.Equal(1, json.GetProperty("sync").GetProperty("needingConfirmation").GetInt32());

        var tasks = TryGetCollection<BsonDocument>(MongoCollections.Tasks)!;
        var matter = await tasks
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .FirstOrDefaultAsync();

        Assert.Equal("list", matter["kind"].AsString);
        Assert.Equal("floating", matter["timePrecision"].AsString);
        Assert.Equal("low", matter["confidence"].AsString);
    }

    // ---- list --------------------------------------------------------------

    [Fact]
    public async Task lists_nothing_for_an_account_with_no_subscriptions()
    {
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/me/ics-feeds", ObjectId.GenerateNewId()));

        Assert.Single(json.EnumerateObject());
        Assert.Empty(json.GetProperty("feeds").EnumerateArray());
    }

    // ---- sync --------------------------------------------------------------

    [Fact]
    public async Task syncing_an_unknown_feed_is_feed_not_found()
    {
        var response = await SendAsync(
            HttpMethod.Post, "/me/ics-feeds/6a78c437aa461ae1dc64ffff/sync", ObjectId.GenerateNewId());

        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("feed_not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("That calendar is not connected.", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("POST", "/me/ics-feeds/not-an-objectid/sync")]
    [InlineData("DELETE", "/me/ics-feeds/zzz")]
    public async Task a_malformed_id_is_the_generic_cast_404_not_the_routes_own(string method, string path)
    {
        // Mongoose's CastError, not the route's feed_not_found. Two different codes
        // the client branches on. Verified live.
        var response = await SendAsync(new HttpMethod(method), path, ObjectId.GenerateNewId());
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Not found", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task the_feed_lookup_beats_the_timezone_check_on_sync()
    {
        // The opposite order to subscribe: here an unknown id 404s even for an account
        // with no timezone at all.
        var response = await SendAsync(
            HttpMethod.Post, "/me/ics-feeds/6a78c437aa461ae1dc64ffff/sync", ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- delete ------------------------------------------------------------

    [Fact]
    public async Task deleting_is_NOT_idempotent()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        _factory.Transport.Calendar(url, Calendar);

        var subscribed = await ReadJsonAsync(
            await PostAsync("/me/ics-feeds", userId, new { url, label = "Term", domain = "family" }));

        var id = subscribed.GetProperty("feed").GetProperty("id").GetString();

        var first = await SendAsync(HttpMethod.Delete, $"/me/ics-feeds/{id}", userId);
        var second = await SendAsync(HttpMethod.Delete, $"/me/ics-feeds/{id}", userId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Contrast with the document-scan delete, which IS idempotent. This one 404s.
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(
            "feed_not_found",
            (await ReadJsonAsync(second)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task deleting_retires_future_open_matters_only_and_journals_nothing()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";

        // One occurrence in the future and one in the past. Both land as matters; only
        // the future one is retired.
        _factory.Transport.Calendar(
            url,
            "BEGIN:VCALENDAR\r\n" +
            $"BEGIN:VEVENT\r\nUID:future\r\nDTSTART:{Stamp(30)}\r\nSUMMARY:Later\r\nEND:VEVENT\r\n" +
            $"BEGIN:VEVENT\r\nUID:past\r\nDTSTART:{Stamp(-2)}\r\n" +
            "SUMMARY:Already happened\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n");

        var subscribed = await ReadJsonAsync(
            await PostAsync("/me/ics-feeds", userId, new { url, label = "Term", domain = "family" }));

        Assert.Equal(2, subscribed.GetProperty("sync").GetProperty("created").GetInt32());

        var id = subscribed.GetProperty("feed").GetProperty("id").GetString();
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Delete, $"/me/ics-feeds/{id}", userId));

        Assert.True(json.GetProperty("removed").GetBoolean());
        Assert.Equal(1, json.GetProperty("retiredMatters").GetInt32());

        var tasks = TryGetCollection<BsonDocument>(MongoCollections.Tasks)!;
        var rows = await tasks.Find(Builders<BsonDocument>.Filter.Eq("userId", userId)).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Where(r => r.Contains("deletedAt")));

        // Past matters are a record of what happened; deleting history is not what the
        // user asked for.
        Assert.Equal("Already happened", rows.Single(r => !r.Contains("deletedAt"))["title"].AsString);

        // Deliberately OUTSIDE BulkService: no journal entry, so no undo. Inconsistent
        // with every other task soft-delete, and it is what the reference does.
        var journal = TryGetCollection<BsonDocument>(MongoCollections.TaskBulkOps)!;
        Assert.Equal(0, await journal.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    [Fact]
    public async Task deleting_one_feed_leaves_another_feeds_matters_alone()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);

        var kept = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        var dropped = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        _factory.Transport.Calendar(kept, Calendar);
        _factory.Transport.Calendar(dropped, Calendar);

        await PostAsync("/me/ics-feeds", userId, new { url = kept, label = "Keep", domain = "family" });
        var second = await ReadJsonAsync(
            await PostAsync("/me/ics-feeds", userId, new { url = dropped, label = "Drop", domain = "family" }));

        var id = second.GetProperty("feed").GetProperty("id").GetString();
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Delete, $"/me/ics-feeds/{id}", userId));

        // Scoped by the anchored externalId prefix. An unscoped sweep would retire the
        // other subscription's matters too.
        Assert.Equal(1, json.GetProperty("retiredMatters").GetInt32());

        var tasks = TryGetCollection<BsonDocument>(MongoCollections.Tasks)!;
        var live = await tasks
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", userId),
                Builders<BsonDocument>.Filter.Exists("deletedAt", false)));

        Assert.Equal(1, live);
    }

    // ---- rule 2: a deleted matter stays deleted ----------------------------

    [Fact]
    public async Task a_user_deleted_matter_is_not_resurrected_by_the_next_poll()
    {
        var users = TryGetCollection<BsonDocument>(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users);
        var url = $"https://feeds.example/{Guid.NewGuid():N}.ics";
        _factory.Transport.Calendar(url, Calendar);

        var subscribed = await ReadJsonAsync(
            await PostAsync("/me/ics-feeds", userId, new { url, label = "Term", domain = "family" }));

        var id = subscribed.GetProperty("feed").GetProperty("id").GetString();

        // The user sweeps it away.
        var tasks = TryGetCollection<BsonDocument>(MongoCollections.Tasks)!;
        await tasks.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("userId", userId),
            Builders<BsonDocument>.Update.Set("deletedAt", DateTime.UtcNow));

        var resync = await ReadJsonAsync(await SendAsync(HttpMethod.Post, $"/me/ics-feeds/{id}/sync", userId));

        // Without rule 2 the user would have to sweep it again after every poll.
        Assert.Equal(0, resync.GetProperty("sync").GetProperty("created").GetInt32());
        Assert.Equal(0, resync.GetProperty("sync").GetProperty("updated").GetInt32());
        Assert.Equal(1, await tasks.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    // ---- helpers -----------------------------------------------------------

    private Task<HttpResponseMessage> PostAsync(string path, ObjectId userId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        return SendAsync(request, userId);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ObjectId userId) =>
        SendAsync(new HttpRequestMessage(method, path), userId);

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ObjectId userId)
    {
        var token = KernelPipelineTests.NodeShapedToken(userId.ToString(), $"{userId}@example.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return _factory.CreateApiClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A UTC DTSTART value <paramref name="days"/> from now.</summary>
    private static string Stamp(int days) =>
        DateTime.UtcNow.AddDays(days).ToString("yyyyMMdd'T'HHmmss'Z'");

    private static async Task<ObjectId> SeedUserAsync(IMongoCollection<BsonDocument> users)
    {
        var id = ObjectId.GenerateNewId();

        await users.InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["email"] = $"{id}@example.test",

            // `users` carries a UNIQUE index on identityUserId. Omitting it stores
            // null, and the second seeded user in the database's lifetime collides.
            ["identityUserId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["timezone"] = "Africa/Cairo",
            ["imports"] = new BsonDocument { ["defaultTimeOfDay"] = "09:00" },
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
        });

        return id;
    }

    /// <summary>
    /// A collection on the parity instance, or <see langword="null"/> when it is not
    /// running — the suite stays green on a machine without it.
    /// </summary>
    private static IMongoCollection<T>? TryGetCollection<T>(string name)
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(IcsWebApplicationFactory.IcsDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database.GetCollection<T>(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
