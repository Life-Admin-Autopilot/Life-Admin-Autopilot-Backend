using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Account;

/// <summary>
/// Its own database, so a parallel slice's test run cannot see these rows.
/// Everything else is the kernel fixture's default.
/// </summary>
public sealed class AccountWebApplicationFactory : KernelWebApplicationFactory
{
    public const string AccountDatabase = "kitto_parity_dotnet_a_tests";

    public AccountWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", AccountDatabase);
    }
}

/// <summary>
/// <c>GET /me/subscription</c>, <c>GET /me/billing/invoices</c> and
/// <c>GET /me/integrations</c>, against the live Node behaviour captured on the
/// reference server.
///
/// <para>
/// The Mongo-backed cases skip (rather than fail) when the parity instance is not
/// running, following <c>UsageQuotaTests.TryCreateStore</c>. The auth and stub
/// cases need no database and always run.
/// </para>
/// </summary>
public sealed class AccountEndpointTests : IClassFixture<AccountWebApplicationFactory>
{
    private readonly AccountWebApplicationFactory _factory;

    public AccountEndpointTests(AccountWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- GET /me/subscription ---------------------------------------------

    [Fact]
    public async Task returns_the_free_tier_and_nothing_else_for_a_new_account()
    {
        // Arrange
        var users = TryGetUsers();
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users, new SubscriptionStateDocument { Tier = "free" });

        // Act
        var json = await GetJsonAsync("/me/subscription", userId, HttpStatusCode.OK);

        // Assert — {"subscription":{"tier":"free"}} and literally nothing more.
        var subscription = json.GetProperty("subscription");
        Assert.Single(json.EnumerateObject());
        Assert.Single(subscription.EnumerateObject());
        Assert.Equal("free", subscription.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task omits_renewsAt_and_canceledAt_rather_than_emitting_null()
    {
        // Arrange — the silent parity break: status and field names match, and only
        // the presence of two null keys differs.
        var users = TryGetUsers();
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users, new SubscriptionStateDocument { Tier = "pro" });

        // Act
        var json = await GetJsonAsync("/me/subscription", userId, HttpStatusCode.OK);
        var subscription = json.GetProperty("subscription");

        // Assert
        Assert.False(subscription.TryGetProperty("renewsAt", out _));
        Assert.False(subscription.TryGetProperty("canceledAt", out _));
    }

    [Fact]
    public async Task emits_timestamps_in_the_javascript_iso_format()
    {
        // Arrange — 600ms specifically: STJ's default writes ".6", Date#toISOString
        // writes ".600", and the harness compares bytes.
        var users = TryGetUsers();
        if (users is null)
        {
            return;
        }

        var userId = await SeedUserAsync(users, new SubscriptionStateDocument
        {
            Tier = "pro",
            RenewsAt = new DateTime(2026, 12, 1, 8, 30, 0, 600, DateTimeKind.Utc),
            CanceledAt = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        // Act
        var json = await GetJsonAsync("/me/subscription", userId, HttpStatusCode.OK);
        var subscription = json.GetProperty("subscription");

        // Assert
        Assert.Equal("2026-12-01T08:30:00.600Z", subscription.GetProperty("renewsAt").GetString());
        Assert.Equal("2026-11-15T00:00:00.000Z", subscription.GetProperty("canceledAt").GetString());

        // Declaration order is the Mongoose schema order.
        Assert.Equal(
            new[] { "tier", "renewsAt", "canceledAt" },
            subscription.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task falls_back_to_the_free_tier_when_the_row_has_no_subscription()
    {
        // Arrange — a row written before the field existed. Node's `?? { tier:'free' }`.
        var users = TryGetUsers();
        if (users is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await users.InsertOneAsync(new BsonDocument
        {
            ["_id"] = userId,
            ["email"] = $"legacy-{userId}@example.test",
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
        });

        // Act
        var json = await GetJsonAsync("/me/subscription", userId, HttpStatusCode.OK);

        // Assert
        Assert.Equal("free", json.GetProperty("subscription").GetProperty("tier").GetString());
    }

    [Fact]
    public async Task answers_404_user_not_found_when_the_account_is_gone()
    {
        // Arrange — a valid token naming a row that no longer exists. No seeding: the
        // id is simply one nothing was ever written under.
        var response = await SendAsync("/me/subscription", ObjectId.GenerateNewId());
        var json = await ReadJsonAsync(response);

        // Assert — the message is contract, verbatim from me.subscription.ts.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("user_not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Account no longer exists.", json.GetProperty("error").GetProperty("message").GetString());
        Assert.False(json.GetProperty("error").TryGetProperty("details", out _));
    }

    // ---- The two stubs ----------------------------------------------------

    [Theory]
    [InlineData("/me/billing/invoices", "invoices")]
    [InlineData("/me/integrations", "integrations")]
    public async Task the_stubs_return_a_single_empty_list(string path, string field)
    {
        // Act — deliberately an id with no row behind it: these never load the user.
        var json = await GetJsonAsync(path, ObjectId.GenerateNewId(), HttpStatusCode.OK);

        // Assert
        Assert.Single(json.EnumerateObject());
        var items = json.GetProperty(field);
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Empty(items.EnumerateArray());
    }

    [Theory]
    [InlineData("/me/billing/invoices")]
    [InlineData("/me/integrations")]
    public async Task the_stubs_still_answer_200_for_a_deleted_account(string path)
    {
        // Arrange — after DELETE /me the access token stays valid until it expires.
        // /me/subscription then 404s while these two keep answering. Adding a user
        // lookup here "for consistency" would break parity.
        var deletedUser = ObjectId.GenerateNewId();

        // Act
        var stub = await SendAsync(path, deletedUser);
        var userBacked = await SendAsync("/me/subscription", deletedUser);

        // Assert
        Assert.Equal(HttpStatusCode.OK, stub.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, userBacked.StatusCode);
    }

    // ---- Auth -------------------------------------------------------------

    [Theory]
    [InlineData("/me/subscription")]
    [InlineData("/me/billing/invoices")]
    [InlineData("/me/integrations")]
    public async Task rejects_a_missing_authorization_header(string path)
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync(path);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Missing access token", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/me/subscription")]
    [InlineData("/me/billing/invoices")]
    [InlineData("/me/integrations")]
    public async Task rejects_a_non_bearer_scheme_as_a_missing_token(string path)
    {
        // Arrange — Node tests `startsWith('Bearer ')`, so `Basic …` is MISSING, not
        // invalid. The two codes are different strings the client branches on.
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Basic abc");

        // Act
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/me/subscription")]
    [InlineData("/me/billing/invoices")]
    [InlineData("/me/integrations")]
    public async Task rejects_a_malformed_token_as_an_invalid_token(string path)
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        // Act
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_token", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Invalid or expired access token",
            json.GetProperty("error").GetProperty("message").GetString());
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<JsonElement> GetJsonAsync(string path, ObjectId userId, HttpStatusCode expected)
    {
        var response = await SendAsync(path, userId);
        Assert.Equal(expected, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, ObjectId userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = KernelPipelineTests.NodeShapedToken(userId.ToString(), $"{userId}@example.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _factory.CreateApiClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<ObjectId> SeedUserAsync(
        IMongoCollection<BsonDocument> users,
        SubscriptionStateDocument subscription)
    {
        var id = ObjectId.GenerateNewId();

        await users.InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["email"] = $"{id}@example.test",
            ["subscription"] = SubscriptionBson(subscription),
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
        });

        return id;
    }

    /// <summary>
    /// Written as raw BSON, omitting unset optionals exactly as Mongoose does — a
    /// stored <c>null</c> would not exercise the same read path.
    /// </summary>
    private static BsonDocument SubscriptionBson(SubscriptionStateDocument subscription)
    {
        var document = new BsonDocument { ["tier"] = subscription.Tier };

        if (subscription.RenewsAt is { } renewsAt)
        {
            document["renewsAt"] = renewsAt;
        }

        if (subscription.CanceledAt is { } canceledAt)
        {
            document["canceledAt"] = canceledAt;
        }

        return document;
    }

    /// <summary>
    /// The <c>users</c> collection on the parity instance, or <see langword="null"/>
    /// when it is not running — the suite stays green on a machine without it.
    /// </summary>
    private static IMongoCollection<BsonDocument>? TryGetUsers()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(AccountWebApplicationFactory.AccountDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database.GetCollection<BsonDocument>(MongoCollections.Users);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
