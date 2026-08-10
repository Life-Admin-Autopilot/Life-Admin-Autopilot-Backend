using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.GoogleIntegration;

/// <summary>
/// Its own database, so a parallel slice's test run cannot see these rows.
/// </summary>
public sealed class GoogleWebApplicationFactory : KernelWebApplicationFactory
{
    public const string GoogleDatabase = "kitto_parity_dotnet_g_tests";

    public GoogleWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", GoogleDatabase);
    }
}

/// <summary>
/// The five Google routes in the state the reference server can actually reach: no
/// <c>GOOGLE_CLIENT_ID</c>/<c>SECRET</c>/<c>REDIRECT_URI</c> and no
/// <c>INTEGRATION_ENCRYPTION_KEY</c>, so <c>ready()</c> is false.
///
/// <para>
/// Everything asserted here was captured live against <c>:4200</c>. The success
/// paths of <c>POST /authorize</c>, <c>POST /sync</c> and the connected callback
/// cannot be reached without real Google credentials and are NOT covered.
/// </para>
/// </summary>
public sealed class GoogleIntegrationEndpointTests : IClassFixture<GoogleWebApplicationFactory>
{
    private readonly GoogleWebApplicationFactory _factory;

    public GoogleIntegrationEndpointTests(GoogleWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- GET /integrations/google -----------------------------------------

    [Fact]
    public async Task reports_unavailable_and_no_integration_when_google_is_not_configured()
    {
        // Arrange — the VERIFIED live shape: {"available":false,"integration":null}.
        // Both keys always present, and `integration` an explicit null rather than an
        // omitted key.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Get, "/integrations/google");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, json.EnumerateObject().Count());
        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("integration").ValueKind);
        Assert.Equal("available,integration", string.Join(',', json.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task requires_a_token_on_every_route_except_the_callback()
    {
        // Arrange
        using var client = _factory.CreateApiClient();

        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/integrations/google"),
            (HttpMethod.Post, "/integrations/google/authorize"),
            (HttpMethod.Post, "/integrations/google/sync"),
            (HttpMethod.Delete, "/integrations/google"),
        };

        foreach (var (method, path) in routes)
        {
            // Act
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("missing_token", await ErrorCode(response));
        }
    }

    // ---- POST /integrations/google/authorize -------------------------------

    [Fact]
    public async Task authorize_is_400_google_not_configured_not_503()
    {
        // Arrange — the readiness gate answers 400, NOT the 503 the AI routes use for
        // the same "not configured on this server" situation. Verified live.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(
            client,
            HttpMethod.Post,
            "/integrations/google/authorize",
            JsonContent.Create(new { web = true }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");

        Assert.Equal("google_not_configured", error.GetProperty("code").GetString());
        Assert.Equal(
            "Connecting a Google account is not available yet.",
            error.GetProperty("message").GetString());

        // `details` is omitted entirely, never null.
        Assert.False(error.TryGetProperty("details", out _));
    }

    [Fact]
    public async Task authorize_accepts_a_missing_body()
    {
        // Arrange — there is NO zod schema on this route; `web` simply defaults to
        // false when the body is absent. It must not become a validation error.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Post, "/integrations/google/authorize");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("google_not_configured", await ErrorCode(response));
    }

    [Fact]
    public async Task authorize_ignores_unknown_body_keys()
    {
        // Arrange — the binder is LENIENT here. A `.strict()` binder would answer
        // invalid_body to a payload Node accepts.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(
            client,
            HttpMethod.Post,
            "/integrations/google/authorize",
            JsonContent.Create(new { web = true, bogus = 1, nested = new { a = 2 } }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("google_not_configured", await ErrorCode(response));
    }

    [Theory]
    [InlineData("{\"web\":\"yes\"}")]
    [InlineData("{\"web\":1}")]
    [InlineData("{\"web\":null}")]
    [InlineData("{\"web\":false}")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    public async Task authorize_ignores_every_body_shape_express_json_tolerates(string body)
    {
        // Arrange — the route reads `req.body?.web === true` with no schema in front
        // of it, so a wrong TYPE is not a validation error. Measured against :4200:
        // all six of these answer google_not_configured. A typed body binder answers
        // 400 invalid_body to three of them, which is why this route reads the JSON
        // by hand.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(
            client,
            HttpMethod.Post,
            "/integrations/google/authorize",
            new StringContent(body, Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("google_not_configured", await ErrorCode(response));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"hello\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{")]
    public async Task authorize_is_500_for_a_body_express_json_refuses(string body)
    {
        // Arrange — `express.json()` defaults to strict:true, so only an object or an
        // array may be the top-level value. Its SyntaxError is unrecognised by Node's
        // error handler and falls through to the generic 500 — one of the parity
        // traps: the "correct" answer of 400 is wrong here.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(
            client,
            HttpMethod.Post,
            "/integrations/google/authorize",
            new StringContent(body, Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", await ErrorCode(response));
    }

    // ---- POST /integrations/google/sync and DELETE -------------------------

    [Fact]
    public async Task sync_is_404_not_connected_when_no_row_exists()
    {
        // Arrange
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Post, "/integrations/google/sync");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_connected", await ErrorCode(response));
        Assert.Equal("No Google account is connected.", await ErrorMessage(response));
    }

    [Fact]
    public async Task disconnect_is_404_not_connected_when_no_row_exists()
    {
        // Arrange
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Delete, "/integrations/google");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_connected", await ErrorCode(response));
    }

    [Fact]
    public async Task disconnect_is_not_idempotent()
    {
        // Arrange — the parity trap: the document-scan DELETE next door IS idempotent
        // and this one is NOT. Seeded directly, because reaching a connected state
        // through the API needs Google credentials.
        var integrations = TryGetIntegrations();
        if (integrations is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await integrations.InsertOneAsync(SeedRow(userId));

        using var client = _factory.CreateApiClient();

        // Act
        using var first = await Authed(client, HttpMethod.Delete, "/integrations/google", user: userId);
        using var second = await Authed(client, HttpMethod.Delete, "/integrations/google", user: userId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("{\"removed\":true}", await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal("not_connected", await ErrorCode(second));
    }

    [Fact]
    public async Task sync_is_400_timezone_required_before_it_touches_google()
    {
        // Arrange — order matters: integration lookup first (404), THEN the timezone
        // check (400). A connected row with no user profile must reach the 400 rather
        // than attempting a token refresh.
        var integrations = TryGetIntegrations();
        if (integrations is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await integrations.InsertOneAsync(SeedRow(userId));

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Post, "/integrations/google/sync", user: userId);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("timezone_required", await ErrorCode(response));
        Assert.Equal(
            "Set your timezone before importing — Google items have no timezone of their own.",
            await ErrorMessage(response));

        await integrations.DeleteManyAsync(Builders<IntegrationDocument>.Filter.Eq(i => i.UserId, userId));
    }

    [Fact]
    public async Task get_never_serialises_token_material()
    {
        // Arrange — belt and braces. The row carries all three secret fields; the
        // response must carry none of them, and the user id must be the ObjectId
        // string rather than an extended-JSON object.
        var integrations = TryGetIntegrations();
        if (integrations is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await integrations.InsertOneAsync(SeedRow(userId));

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Get, "/integrations/google", user: userId);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("refreshTokenEnc", body, StringComparison.Ordinal);
        Assert.DoesNotContain("accessTokenEnc", body, StringComparison.Ordinal);
        Assert.DoesNotContain("accessTokenExpiresAt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("v1.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_id", body, StringComparison.Ordinal);

        var integration = JsonDocument.Parse(body).RootElement.GetProperty("integration");
        Assert.Equal(userId.ToString(), integration.GetProperty("userId").GetString());
        Assert.Equal("google", integration.GetProperty("provider").GetString());

        // `id` is appended by the toJSON transform after `_id` is deleted, so it is
        // the LAST key.
        Assert.Equal("id", integration.EnumerateObject().Last().Name);

        await integrations.DeleteManyAsync(Builders<IntegrationDocument>.Filter.Eq(i => i.UserId, userId));
    }

    [Fact]
    public async Task sync_reports_both_sub_syncs_skipped_when_no_scope_was_granted()
    {
        // Arrange — the one sync SUCCESS body reachable with no Google credentials:
        // the scope gate short-circuits both sub-syncs before any HTTP call. Captured
        // byte-for-byte from a Node reference configured with fake credentials and
        // the same seeded row.
        var integrations = TryGetIntegrations();
        if (integrations is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedUserWithTimezoneAsync(userId);
        await integrations.InsertOneAsync(SeedRow(userId));

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Post, "/integrations/google/sync", user: userId);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("integration,calendar,tasks", string.Join(',', json.EnumerateObject().Select(p => p.Name)));

        var calendar = json.GetProperty("calendar");
        Assert.Equal(
            "status,created,updated,commitments,ignored,fullResync,reason",
            string.Join(',', calendar.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("skipped", calendar.GetProperty("status").GetString());
        Assert.Equal("Calendar access was not granted.", calendar.GetProperty("reason").GetString());
        Assert.False(calendar.GetProperty("fullResync").GetBoolean());

        var tasks = json.GetProperty("tasks");
        Assert.Equal("status,created,updated,reason", string.Join(',', tasks.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("Tasks access was not granted.", tasks.GetProperty("reason").GetString());

        await CleanupAsync(integrations, userId);
    }

    [Fact]
    public async Task sync_is_a_500_when_the_connection_already_needs_reauth()
    {
        // Arrange — IntegrationUnavailableError is deliberately NOT an AppError in
        // Node, so it falls through the handler to the generic 500. A tidy 4xx here
        // would be a parity break.
        var integrations = TryGetIntegrations();
        if (integrations is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedUserWithTimezoneAsync(userId);

        var row = SeedRow(userId);
        row.Status = "needs_reauth";
        row.GrantedScopes = new List<string> { ScopeCalendar, ScopeTasks };
        await integrations.InsertOneAsync(row);

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Post, "/integrations/google/sync", user: userId);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", await ErrorCode(response));

        await CleanupAsync(integrations, userId);
    }

    [Fact]
    public async Task sync_flips_the_row_to_needs_reauth_when_the_refresh_token_will_not_decrypt()
    {
        // Arrange — THE transition that decides whether a connection is dead or
        // briefly unhappy. The refresh token is the durable credential: if it will
        // not decrypt, the key changed or the row is corrupt, and no retry fixes it.
        // The cached access token is expired so the code cannot stop at the cache.
        var integrations = TryGetIntegrations();
        if (integrations is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedUserWithTimezoneAsync(userId);

        var row = SeedRow(userId);
        row.GrantedScopes = new List<string> { ScopeCalendar, ScopeTasks };
        row.AccessTokenEnc = "v1.notdecryptable.notdecryptable.notdecryptable";
        row.AccessTokenExpiresAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await integrations.InsertOneAsync(row);

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await Authed(client, HttpMethod.Post, "/integrations/google/sync", user: userId);

        var after = await integrations
            .Find(Builders<IntegrationDocument>.Filter.Eq(i => i.UserId, userId))
            .FirstOrDefaultAsync();

        // Assert — 500 to the caller, and the row is now terminal until the user acts.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("needs_reauth", after.Status);
        Assert.Equal("Stored credentials could not be read.", after.LastError);

        await CleanupAsync(integrations, userId);
    }

    // ---- GET /integrations/google/callback ---------------------------------

    [Theory]
    [InlineData("?error=access_denied&state=irrelevant", "kitto://integrations/google?status=cancelled")]
    [InlineData("?error=weird", "kitto://integrations/google?status=error")]
    [InlineData("", "kitto://integrations/google?status=error&reason=invalid_state")]
    [InlineData("?state=bogus&code=abc", "kitto://integrations/google?status=error&reason=invalid_state")]
    public async Task callback_redirects_with_the_body_express_writes(string query, string expected)
    {
        // Arrange — all four captured byte-for-byte from :4200. res.redirect writes a
        // text/plain body and a Content-Length, which Results.Redirect does not.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await client.GetAsync($"/integrations/google/callback{query}");
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal($"Found. Redirecting to {expected}", body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task callback_is_reachable_without_a_token()
    {
        // Arrange — UNAUTHENTICATED by necessity: Google's redirect carries no cookie
        // and no Authorization header. The signed state is the only thing binding the
        // incoming tokens to an account.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await client.GetAsync("/integrations/google/callback?error=access_denied");

        // Assert — a 401 here would break the whole flow.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    [Fact]
    public async Task callback_writes_the_html_body_when_the_client_prefers_html()
    {
        // Arrange — a real browser lands here with Accept: text/html, and express's
        // res.format switches the 302 body accordingly. Express 5 emits no <a>.
        using var client = _factory.CreateApiClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/integrations/google/callback?error=access_denied");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "<p>Found. Redirecting to kitto://integrations/google?status=cancelled</p>",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task callback_writes_no_body_when_neither_text_nor_html_is_acceptable()
    {
        // Arrange — res.format's `default` branch: empty body, Content-Length 0, and
        // no Content-Type at all.
        using var client = _factory.CreateApiClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/integrations/google/callback?error=access_denied");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Null(response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task callback_treats_a_repeated_state_parameter_as_absent()
    {
        // Arrange — express parses `?state=a&state=b` into an ARRAY, and the route
        // guards with `typeof === 'string'`, so it reads as undefined. Taking the
        // first value instead would let a caller smuggle a valid state past a decoy.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await client.GetAsync("/integrations/google/callback?state=a&state=b&code=x");

        // Assert
        Assert.Equal(
            "kitto://integrations/google?status=error&reason=invalid_state",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task callback_treats_an_empty_error_parameter_as_absent()
    {
        // Arrange — `if (declined)` in Node, and the empty string is falsy, so
        // `?error=` must fall through to state verification rather than exiting as a
        // cancellation.
        using var client = _factory.CreateApiClient();

        // Act
        using var response = await client.GetAsync("/integrations/google/callback?error=");

        // Assert
        Assert.Equal(
            "kitto://integrations/google?status=error&reason=invalid_state",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task callback_answers_no_code_for_a_valid_state_with_no_code()
    {
        // Arrange — the one failure exit that needs a genuinely valid state, so it is
        // minted here with the fixture's own JWT secret rather than curled.
        var state = new GoogleOAuthState(
                new GoogleIntegrationOptions { AccessSecret = KernelWebApplicationFactory.TestJwtSecret },
                TimeProvider.System)
            .Issue(ObjectId.GenerateNewId().ToString());

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await client.GetAsync(
            $"/integrations/google/callback?state={Uri.EscapeDataString(state)}");

        // Assert
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "kitto://integrations/google?status=error&reason=no_code",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task callback_answers_exchange_failed_when_google_is_not_configured()
    {
        // Arrange — a valid state plus a code, on a server that cannot exchange it.
        // `config()` throws GoogleNotConfiguredException inside the try, which the
        // route converts into a redirect rather than letting a 500 envelope escape.
        var state = new GoogleOAuthState(
                new GoogleIntegrationOptions { AccessSecret = KernelWebApplicationFactory.TestJwtSecret },
                TimeProvider.System)
            .Issue(ObjectId.GenerateNewId().ToString());

        using var client = _factory.CreateApiClient();

        // Act
        using var response = await client.GetAsync(
            $"/integrations/google/callback?state={Uri.EscapeDataString(state)}&code=abc123");

        // Assert
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "kitto://integrations/google?status=error&reason=exchange_failed",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task callback_never_emits_the_json_error_envelope()
    {
        // Arrange — the contract's headline rule for this route.
        using var client = _factory.CreateApiClient();

        var queries = new[]
        {
            string.Empty,
            "?error=access_denied",
            "?state=forged.signature",
            "?code=only",
        };

        foreach (var query in queries)
        {
            // Act
            using var response = await client.GetAsync($"/integrations/google/callback{query}");
            var body = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
            Assert.StartsWith("kitto://integrations/google?", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }
    }

    // ---- helpers -----------------------------------------------------------

    private const string ScopeCalendar = "https://www.googleapis.com/auth/calendar.readonly";
    private const string ScopeTasks = "https://www.googleapis.com/auth/tasks.readonly";

    /// <summary>
    /// The sync route needs a <c>timezone</c> on the caller's row. <c>PATCH /me</c>
    /// belongs to another slice, so the field is written straight to Mongo — a raw
    /// document, because only two of the profile's fields matter here.
    /// </summary>
    private static async Task SeedUserWithTimezoneAsync(ObjectId userId)
    {
        var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        var users = new MongoClient(settings)
            .GetDatabase(GoogleWebApplicationFactory.GoogleDatabase)
            .GetCollection<BsonDocument>(MongoCollections.Users);

        await users.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("_id", userId));
        await users.InsertOneAsync(new BsonDocument
        {
            ["_id"] = userId,
            ["email"] = "google@probe.com",
            ["timezone"] = "Africa/Cairo",
            ["imports"] = new BsonDocument { ["defaultTimeOfDay"] = "09:00" },
        });
    }

    private static async Task CleanupAsync(IMongoCollection<IntegrationDocument> integrations, ObjectId userId)
    {
        await integrations.DeleteManyAsync(Builders<IntegrationDocument>.Filter.Eq(i => i.UserId, userId));

        var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        await new MongoClient(settings)
            .GetDatabase(GoogleWebApplicationFactory.GoogleDatabase)
            .GetCollection<BsonDocument>(MongoCollections.Users)
            .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("_id", userId));
    }

    private static IntegrationDocument SeedRow(ObjectId userId) => new()
    {
        Id = ObjectId.GenerateNewId(),
        UserId = userId,
        Provider = "google",
        ExternalAccountId = "google-sub-123",
        ExternalAccountEmail = "someone@example.com",
        RefreshTokenEnc = "v1.aaaa.bbbb.cccc",
        AccessTokenEnc = "v1.dddd.eeee.ffff",
        AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
        GrantedScopes = new List<string> { "openid", "email" },
        Status = "active",
        ImportDomain = "home",
        ConnectedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private Task<HttpResponseMessage> Authed(
        HttpClient client,
        HttpMethod method,
        string path,
        HttpContent? content = null,
        ObjectId? user = null)
    {
        var userId = (user ?? ObjectId.GenerateNewId()).ToString();
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(
            "Authorization",
            $"Bearer {KernelPipelineTests.NodeShapedToken(userId, "google@probe.com")}");

        return client.SendAsync(request);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("code").GetString();

    private static async Task<string?> ErrorMessage(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetProperty("message").GetString();

    /// <summary>
    /// Mongo-backed cases skip rather than fail when the parity instance is not
    /// running, following <c>UsageQuotaTests.TryCreateStore</c>.
    /// </summary>
    private static IMongoCollection<IntegrationDocument>? TryGetIntegrations()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            settings.ConnectTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(GoogleWebApplicationFactory.GoogleDatabase);

            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database.GetCollection<IntegrationDocument>(MongoCollections.Integrations);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
