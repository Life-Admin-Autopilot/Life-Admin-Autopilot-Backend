using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Profile;

/// <summary>Its own database, so a parallel slice's run cannot see these rows.</summary>
public sealed class ProfileWebApplicationFactory : KernelWebApplicationFactory
{
    public const string ProfileDatabase = "kitto_parity_dotnet_k_tests";

    public ProfileWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", ProfileDatabase);
    }
}

/// <summary>
/// <c>PATCH /me</c>, <c>DELETE /me</c> and <c>GET /me/export</c> end to end.
///
/// <para>
/// Mongo-backed cases skip (rather than fail) when the parity instance is not
/// running, following <c>UsageQuotaTests.TryCreateStore</c>; the auth cases need no
/// database and always run.
/// </para>
/// </summary>
public sealed class ProfileEndpointTests : IClassFixture<ProfileWebApplicationFactory>
{
    private readonly ProfileWebApplicationFactory _factory;

    public ProfileEndpointTests(ProfileWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- PATCH /me ---------------------------------------------------------

    [Fact]
    public async Task patching_one_nested_key_leaves_its_siblings_untouched()
    {
        // Arrange — the behaviour the whole route is shaped around.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        var json = await SendJsonAsync(HttpMethod.Patch, "/me", userId, """{"notifications":{"push":false}}""", HttpStatusCode.OK);
        var notifications = json.GetProperty("user").GetProperty("notifications");

        // Assert
        Assert.False(notifications.GetProperty("push").GetBoolean());
        Assert.True(notifications.GetProperty("emailDigest").GetBoolean());
        Assert.False(notifications.GetProperty("marketing").GetBoolean());
    }

    [Fact]
    public async Task patching_writes_dot_notation_so_the_stored_subdocument_keeps_its_other_keys()
    {
        // Arrange — the wire check above passes even for a wholesale replace when
        // the replaced values happen to equal the defaults. This one reads the row
        // back after changing a sibling first, which a wholesale replace cannot pass.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        await SendJsonAsync(HttpMethod.Patch, "/me", userId, """{"notifications":{"marketing":true}}""", HttpStatusCode.OK);
        var json = await SendJsonAsync(HttpMethod.Patch, "/me", userId, """{"notifications":{"push":false}}""", HttpStatusCode.OK);
        var notifications = json.GetProperty("user").GetProperty("notifications");

        // Assert — marketing must have survived the second patch.
        Assert.False(notifications.GetProperty("push").GetBoolean());
        Assert.True(notifications.GetProperty("marketing").GetBoolean());
    }

    [Fact]
    public async Task an_empty_body_is_a_touch_that_bumps_updated_at()
    {
        // Arrange — Mongoose's `timestamps: true` injects updatedAt INTO the $set,
        // so even an empty patch moves it. The .NET driver does not, and a
        // line-by-line port therefore leaves the field stale.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);
        var before = (await SendJsonAsync(HttpMethod.Patch, "/me", userId, "{}", HttpStatusCode.OK))
            .GetProperty("user").GetProperty("updatedAt").GetString();

        await Task.Delay(5);

        // Act
        var after = (await SendJsonAsync(HttpMethod.Patch, "/me", userId, "{}", HttpStatusCode.OK))
            .GetProperty("user").GetProperty("updatedAt").GetString();

        // Assert
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task stores_a_whitespace_only_display_name_as_the_empty_string()
    {
        // Arrange — `.min(1).max(80).trim()` with the length check FIRST.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        var json = await SendJsonAsync(HttpMethod.Patch, "/me", userId, """{"displayName":"   "}""", HttpStatusCode.OK);

        // Assert
        Assert.Equal(string.Empty, json.GetProperty("user").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task never_emits_the_password_hash()
    {
        // Arrange
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: true);

        // Act
        var json = await SendJsonAsync(HttpMethod.Patch, "/me", userId, "{}", HttpStatusCode.OK);
        var user = json.GetProperty("user");

        // Assert
        Assert.False(user.TryGetProperty("passwordHash", out _));
        Assert.True(user.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task answers_404_when_the_account_behind_a_valid_token_is_gone()
    {
        // Act — the token stays cryptographically valid after DELETE /me, so this is
        // a routine 404 rather than an auth failure.
        var response = await SendAsync(HttpMethod.Patch, "/me", ObjectId.GenerateNewId(), "{}");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("user_not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Account no longer exists.", json.GetProperty("error").GetProperty("message").GetString());
    }

    // ---- DELETE /me --------------------------------------------------------

    [Fact]
    public async Task deleting_a_passwordless_account_needs_no_confirmation()
    {
        // Arrange — a magic-link account has no credential, so demanding a password
        // would ask for something the user cannot give.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: false);

        // Act
        var response = await SendAsync(HttpMethod.Delete, "/me", userId, "{}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await db.GetCollection<BsonDocument>(MongoCollections.Users)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", userId)));
    }

    [Fact]
    public async Task deleting_an_account_with_a_password_and_no_password_supplied_is_400()
    {
        // Arrange
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: true);

        // Act
        var response = await SendAsync(HttpMethod.Delete, "/me", userId, "{}");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_required", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Enter your password to delete your account.",
            json.GetProperty("error").GetProperty("message").GetString());
        Assert.False(json.GetProperty("error").TryGetProperty("details", out _));
    }

    [Fact]
    public async Task a_missing_delete_body_behaves_exactly_like_an_empty_one()
    {
        // Arrange — a JSON body on a DELETE, which several stacks drop.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: true);

        // Act — no body and no content type at all.
        var response = await SendAsync(HttpMethod.Delete, "/me", userId, body: null);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_required", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"password":""}""", "String must contain at least 1 character(s)")]
    [InlineData("""{"password":123}""", "Expected string, received number")]
    public async Task a_malformed_password_is_invalid_body_not_password_required(string body, string expected)
    {
        // Arrange — the two are different responses and the client branches on them.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: true);

        // Act
        var response = await SendAsync(HttpMethod.Delete, "/me", userId, body);
        var json = await ReadJsonAsync(response);
        var error = json.GetProperty("error");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal("That delete request didn't look right.", error.GetProperty("message").GetString());
        Assert.Equal(
            expected,
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task an_unknown_key_in_the_delete_body_is_stripped_not_rejected()
    {
        // Arrange — the delete schema is lenient too.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: false);

        // Act
        var response = await SendAsync(HttpMethod.Delete, "/me", userId, """{"bogus":1}""");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task the_cascade_removes_every_registered_slice_collection()
    {
        // Arrange — one row per collection an eraser is registered for, plus a row
        // belonging to somebody else in each, which must survive. This is the test
        // that a hardcoded list would have to be edited for; the registry means it
        // simply covers whatever is registered.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: false);
        var bystander = ObjectId.GenerateNewId();

        var collections = new[]
        {
            MongoCollections.Tasks,
            MongoCollections.TaskBulkOps,
            MongoCollections.Notifications,
            MongoCollections.ScannedDocuments,
            MongoCollections.DocumentScanUsageCounters,
            MongoCollections.TranslationUsageCounters,
            MongoCollections.IcsFeeds,
            MongoCollections.Integrations,
            MongoCollections.RefreshTokens,
            MongoCollections.VerificationTokens,
        };

        foreach (var name in collections)
        {
            await db.GetCollection<BsonDocument>(name).InsertManyAsync(new[]
            {
                Row(userId),
                Row(bystander),
            });
        }

        // `tokenHash` is uniquely indexed on both token collections, so two rows
        // that both omit it collide on null before the cascade ever runs.
        static BsonDocument Row(ObjectId owner) => new()
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = owner,
            ["tokenHash"] = ObjectId.GenerateNewId().ToString(),
        };

        // Act
        var response = await SendAsync(HttpMethod.Delete, "/me", userId, "{}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        foreach (var name in collections)
        {
            var collection = db.GetCollection<BsonDocument>(name);
            Assert.Equal(
                0,
                await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
            Assert.Equal(
                1,
                await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", bystander)));
        }
    }

    [Fact]
    public async Task the_cascade_is_re_runnable()
    {
        // Arrange — no transaction is available (standalone mongod), so the contract
        // is idempotence: a mid-cascade failure must be recoverable by retrying.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, hasPassword: false);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/me", userId, "{}")).StatusCode);

        // Act — the row is gone, so the second call is the "already deleted" branch.
        var again = await SendAsync(HttpMethod.Delete, "/me", userId, "{}");
        var json = await ReadJsonAsync(again);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("user_not_found", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- GET /me/export ----------------------------------------------------

    [Fact]
    public async Task the_export_is_an_attachment_with_a_utc_date_stamped_filename()
    {
        // Arrange
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        var response = await SendAsync(HttpMethod.Get, "/me/export", userId, body: null);

        // Assert — the stamp is `new Date().toISOString().slice(0,10)`, i.e. UTC.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"attachment; filename=\"kitto-export-{DateTime.UtcNow:yyyy-MM-dd}.json\"",
            Assert.Single(response.Content.Headers.GetValues("Content-Disposition")));
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task the_export_body_is_pretty_printed_with_two_spaces()
    {
        // Arrange — `JSON.stringify(payload, null, 2)` through res.send(), so the
        // whitespace is part of the bytes a client receives. Results.Json would
        // compact it.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        var text = await (await SendAsync(HttpMethod.Get, "/me/export", userId, body: null)).Content.ReadAsStringAsync();
        var lines = text.Split('\n');

        // Assert
        Assert.StartsWith("{\n", text);
        Assert.StartsWith("  \"exportedAt\":", lines[1]);
        Assert.StartsWith("  \"version\": 1", lines[2]);
    }

    [Fact]
    public async Task the_export_carries_all_eleven_sections_and_the_user()
    {
        // Arrange
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/me/export", userId, body: null));

        // Assert — order is the reference's, and VerificationToken is deliberately
        // absent: it is a credential, so it is not exported at all.
        Assert.Equal(
            new[]
            {
                "exportedAt", "version", "user",
                "matters", "bulkOperations", "voiceNotes", "documents", "conversations",
                "clarifications", "dailyDigests", "notifications", "aiUsage",
                "documentScanUsage", "sessions",
            },
            json.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal(1, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task the_export_ships_raw_lean_documents_with_id_and_version_intact()
    {
        // Arrange — only `user` is toJSON'd. Everything else keeps `_id`, `__v` and
        // `userId` exactly as stored.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);
        var taskId = ObjectId.GenerateNewId();
        await db.GetCollection<BsonDocument>(MongoCollections.Tasks).InsertOneAsync(new BsonDocument
        {
            ["_id"] = taskId,
            ["userId"] = userId,
            ["title"] = "Exported",
            ["createdAt"] = new DateTime(2026, 3, 1, 9, 0, 0, 600, DateTimeKind.Utc),
            ["__v"] = 0,
        });

        // Act
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/me/export", userId, body: null));
        var matter = json.GetProperty("matters").GetProperty("items")[0];

        // Assert
        Assert.Equal(taskId.ToString(), matter.GetProperty("_id").GetString());
        Assert.Equal(userId.ToString(), matter.GetProperty("userId").GetString());
        Assert.Equal(0, matter.GetProperty("__v").GetInt32());
        Assert.False(matter.TryGetProperty("id", out _));

        // Three fractional digits, as Date#toISOString writes them.
        Assert.Equal("2026-03-01T09:00:00.600Z", matter.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task the_export_projects_out_the_storage_key_and_the_token_hash()
    {
        // Arrange — the two rules: no blobs, no credentials.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        await db.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments).InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["storageKey"] = "scans/secret-path.pdf",
            ["rawExtractedText"] = "kept",
        });

        await db.GetCollection<BsonDocument>(MongoCollections.RefreshTokens).InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            // Unique per run: tokenHash is uniquely indexed, so a literal would
            // collide with the row left behind by the previous run.
            ["tokenHash"] = $"the-credential-{ObjectId.GenerateNewId()}",
            ["replacedBy"] = "chained",
            ["userAgent"] = "kept",
            ["revokedAt"] = DateTime.UtcNow,
        });

        // Act
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/me/export", userId, body: null));
        var document = json.GetProperty("documents").GetProperty("items")[0];
        var session = json.GetProperty("sessions").GetProperty("items")[0];

        // Assert
        Assert.False(document.TryGetProperty("storageKey", out _));
        Assert.Equal("kept", document.GetProperty("rawExtractedText").GetString());

        Assert.False(session.TryGetProperty("tokenHash", out _));
        Assert.False(session.TryGetProperty("replacedBy", out _));
        Assert.Equal("kept", session.GetProperty("userAgent").GetString());

        // A revoked session still ships — this list is not filtered the way
        // /auth/sessions/list is.
        Assert.True(session.TryGetProperty("revokedAt", out _));
    }

    [Fact]
    public async Task an_empty_section_reports_zero_and_not_truncated()
    {
        // Arrange
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        // Act
        var json = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/me/export", userId, body: null));
        var section = json.GetProperty("clarifications");

        // Assert
        Assert.Equal(0, section.GetProperty("count").GetInt32());
        Assert.False(section.GetProperty("truncated").GetBoolean());
        Assert.Empty(section.GetProperty("items").EnumerateArray());
    }

    // ---- Auth --------------------------------------------------------------

    [Theory]
    [InlineData("PATCH", "/me")]
    [InlineData("DELETE", "/me")]
    [InlineData("GET", "/me/export")]
    public async Task rejects_a_missing_authorization_header(string method, string path)
    {
        // Act
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("PATCH", "/me")]
    [InlineData("DELETE", "/me")]
    [InlineData("GET", "/me/export")]
    public async Task rejects_a_malformed_token(string method, string path)
    {
        // Act
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task a_text_plain_body_is_not_parsed_at_all()
    {
        // Arrange — express.json() skips any non-application/json request, so the
        // patch is empty and the route answers 200 rather than acting on it. Also a
        // security control: text/plain is a CORS simple request.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db);

        var request = new HttpRequestMessage(HttpMethod.Patch, "/me")
        {
            Content = new StringContent("""{"theme":"dark"}""", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        // Act
        var json = await ReadJsonAsync(await _factory.CreateApiClient().SendAsync(request));

        // Assert
        Assert.Equal("system", json.GetProperty("user").GetProperty("theme").GetString());
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string path,
        ObjectId userId,
        string body,
        HttpStatusCode expected)
    {
        var response = await SendAsync(method, path, userId, body);
        Assert.Equal(expected, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ObjectId userId, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _factory.CreateApiClient().SendAsync(request);
    }

    private static string TokenFor(ObjectId userId) =>
        KernelPipelineTests.NodeShapedToken(userId.ToString(), $"{userId}@example.test");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// A user row shaped the way Mongoose writes one — unset optionals omitted
    /// entirely rather than stored as null.
    /// </summary>
    private static async Task<ObjectId> SeedUserAsync(IMongoDatabase database, bool hasPassword = false)
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        var document = new BsonDocument
        {
            ["_id"] = id,
            // Standard-representation binary, matching what the auth slice writes —
            // the cascade reaches the SQL credential row through this value.
            ["identityUserId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["email"] = $"{id}@example.test",
            ["preferredDomains"] = new BsonArray { "health", "home", "car", "finance", "family", "pets" },
            ["hasOnboarded"] = false,
            ["onboardingAnswers"] = new BsonArray(),
            ["theme"] = "system",
            ["textSize"] = "md",
            ["mic"] = new BsonDocument { ["quality"] = "standard" },
            ["notifications"] = new BsonDocument
            {
                ["push"] = true,
                ["emailDigest"] = true,
                ["marketing"] = false,
            },
            ["imports"] = new BsonDocument { ["defaultTimeOfDay"] = "09:00" },
            ["privacy"] = new BsonDocument { ["analytics"] = true, ["crashReports"] = true },
            ["subscription"] = new BsonDocument { ["tier"] = "free" },
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };

        if (hasPassword)
        {
            // The presence MARKER, not a credential — the real hash lives in Identity.
            document["passwordHash"] = "identity";
        }

        await database.GetCollection<BsonDocument>(MongoCollections.Users).InsertOneAsync(document);
        return id;
    }

    /// <summary>
    /// The parity instance, or <see langword="null"/> when it is not running — the
    /// suite stays green on a machine without it.
    /// </summary>
    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(ProfileWebApplicationFactory.ProfileDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
