using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Notifications;

/// <summary>
/// Its own database, so a parallel slice's test run cannot see these rows.
/// </summary>
public sealed class NotificationsWebApplicationFactory : KernelWebApplicationFactory
{
    public const string NotificationsDatabase = "kitto_parity_dotnet_d_tests";

    public NotificationsWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", NotificationsDatabase);
    }
}

/// <summary>
/// The notifications and reminders endpoints, against behaviour captured live on
/// the reference at <c>:4200</c>.
///
/// <para>
/// Split the way the Matters suite is: <b>auth and validation cases touch no
/// database</b> and always run, while <b>data cases skip</b> (rather than fail)
/// when the parity Mongo is not up — following
/// <c>UsageQuotaTests.TryCreateStore</c>.
/// </para>
/// </summary>
public sealed class NotificationEndpointTests : IClassFixture<NotificationsWebApplicationFactory>
{
    private readonly NotificationsWebApplicationFactory _factory;

    public NotificationEndpointTests(NotificationsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Auth --------------------------------------------------------------

    [Theory]
    [InlineData("/me/notifications")]
    [InlineData("/me/reminders/upcoming")]
    public async Task requires_a_token_on_every_read(string path)
    {
        var response = await _factory.CreateApiClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("missing_token", error.GetProperty("code").GetString());
        Assert.Equal("Missing access token", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task requires_a_token_to_mark_read()
    {
        var response = await _factory.CreateApiClient().PostAsync("/me/notifications/read", JsonBody("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Validation --------------------------------------------------------

    [Fact]
    public async Task renders_a_bad_ids_array_through_the_validation_error_envelope()
    {
        // The one route in the API on the throwing .parse() lane: `details` is an
        // ARRAY of {path,message}, NOT the {formErrors,fieldErrors} object every
        // other route produces. KERNEL.md §2.3.
        var response = await PostReadAsync("""{"ids":[1,2]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("validation_error", error.GetProperty("code").GetString());
        Assert.Equal("Request validation failed", error.GetProperty("message").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(JsonValueKind.Array, details.ValueKind);
        Assert.Equal("ids.0", details[0].GetProperty("path").GetString());
        Assert.Equal("Expected string, received number", details[0].GetProperty("message").GetString());
        Assert.Equal("ids.1", details[1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task rejects_more_than_one_hundred_ids()
    {
        var body = $$"""{"ids":[{{string.Join(',', Enumerable.Repeat("\"a\"", 101))}}]}""";

        var response = await PostReadAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var details = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details");
        Assert.Equal("Array must contain at most 100 element(s)", details[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task answers_five_hundred_for_a_top_level_primitive_body()
    {
        // express.json()'s strict mode rejects a bare primitive as a SyntaxError,
        // which errorHandler.ts does not recognise — so it falls through to the
        // generic 500, not a 400. Verified live for 42, "x" and null.
        var response = await PostReadAsync("42");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task answers_five_hundred_for_malformed_json()
    {
        var response = await PostReadAsync("{not json");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task reports_an_array_body_at_the_empty_path()
    {
        var response = await PostReadAsync("[1,2]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var details = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details");
        Assert.Equal(string.Empty, details[0].GetProperty("path").GetString());
        Assert.Equal("Expected object, received array", details[0].GetProperty("message").GetString());
    }

    // ---- The feed ----------------------------------------------------------

    [Fact]
    public async Task lists_newest_first_with_the_unread_count()
    {
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var now = DateTime.UtcNow;

        await notifications.InsertManyAsync(new[]
        {
            Row(userId, "Oldest", now.AddMinutes(-3)),
            Row(userId, "Newest", now.AddMinutes(-1)),
            Row(userId, "Middle, already read", now.AddMinutes(-2), readAt: now),
        });

        var json = await GetJsonAsync(userId, "/me/notifications");

        var titles = json.GetProperty("notifications").EnumerateArray()
            .Select(n => n.GetProperty("title").GetString())
            .ToArray();

        Assert.Equal(new[] { "Newest", "Middle, already read", "Oldest" }, titles);

        // countDocuments({readAt: null}) — Mongo null-equality matches the rows that
        // never set the field at all, which is how the worker writes them.
        Assert.Equal(2, json.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task never_leaks_another_accounts_notifications()
    {
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var stranger = ObjectId.GenerateNewId();
        await notifications.InsertOneAsync(Row(stranger, "Not yours", DateTime.UtcNow));

        var json = await GetJsonAsync(userId, "/me/notifications");

        Assert.Empty(json.GetProperty("notifications").EnumerateArray());
        Assert.Equal(0, json.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task ignores_unknown_query_parameters_rather_than_rejecting_them()
    {
        // The opposite of the Matters routes: me.notifications has no query schema,
        // so ?bogus=1 answers 200. Verified live.
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);

        await GetJsonAsync(userId, "/me/notifications?bogus=1");
        await GetJsonAsync(userId, "/me/reminders/upcoming?bogus=1");
    }

    [Fact]
    public async Task caps_the_feed_at_fifty_with_no_cursor()
    {
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var now = DateTime.UtcNow;

        await notifications.InsertManyAsync(
            Enumerable.Range(0, 55).Select(i => Row(userId, $"n{i}", now.AddSeconds(-i))));

        var json = await GetJsonAsync(userId, "/me/notifications");

        Assert.Equal(50, json.GetProperty("notifications").GetArrayLength());

        // The count is NOT capped with the page — it counts the whole collection.
        Assert.Equal(55, json.GetProperty("unreadCount").GetInt32());
    }

    // ---- Marking read ------------------------------------------------------

    [Fact]
    public async Task marks_every_unread_row_when_ids_is_omitted()
    {
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var now = DateTime.UtcNow;
        await notifications.InsertManyAsync(new[] { Row(userId, "a", now), Row(userId, "b", now) });

        var json = await PostReadJsonAsync(userId, "{}");

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(0, json.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task marks_every_unread_row_when_ids_is_an_empty_array()
    {
        // `ids && ids.length > 0` — an empty array never narrows the filter, so this
        // is a mark-ALL, not a mark-nothing.
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        await notifications.InsertOneAsync(Row(userId, "a", DateTime.UtcNow));

        Assert.Equal(0, (await PostReadJsonAsync(userId, """{"ids":[]}""")).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task marks_only_the_listed_ids()
    {
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var now = DateTime.UtcNow;
        var target = Row(userId, "target", now);
        await notifications.InsertManyAsync(new[] { target, Row(userId, "spared", now) });

        var json = await PostReadJsonAsync(userId, $$"""{"ids":["{{target["_id"].AsObjectId}}"]}""");

        Assert.Equal(1, json.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task does_nothing_when_every_supplied_id_was_malformed()
    {
        // A non-empty list that filters down to nothing produces an empty $in — a
        // deliberate no-op, NOT a mark-all. That distinction is the whole reason the
        // empty-array case above has to be handled separately.
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        await notifications.InsertOneAsync(Row(userId, "untouched", DateTime.UtcNow));

        var json = await PostReadJsonAsync(userId, """{"ids":["not-an-object-id"]}""");

        Assert.Equal(1, json.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task bumps_updated_at_alongside_read_at()
    {
        // Mongoose adds `updatedAt` to the $set itself for a `timestamps: true`
        // model, so the Node route never mentions it and a literal port leaves the
        // field stale — which the very next GET /me/notifications exposes. Caught by
        // a seeded differential against :4200, where the reference answers
        // updatedAt == readAt to the millisecond.
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var seeded = DateTime.UtcNow.AddDays(-3);
        var row = Row(userId, "stale", seeded);
        await notifications.InsertOneAsync(row);

        await PostReadJsonAsync(userId, "{}");

        var reloaded = await notifications.Find(Builders<BsonDocument>.Filter.Eq("_id", row["_id"])).SingleAsync();

        Assert.Equal(reloaded["readAt"].ToUniversalTime(), reloaded["updatedAt"].ToUniversalTime());
        Assert.True(reloaded["updatedAt"].ToUniversalTime() > seeded.AddMinutes(1));

        // createdAt is untouched.
        Assert.Equal(seeded, reloaded["createdAt"].ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task cannot_mark_another_accounts_notification_read()
    {
        var notifications = TryGetNotifications();
        if (notifications is null)
        {
            return;
        }

        var userId = await ResetUserAsync(notifications);
        var stranger = ObjectId.GenerateNewId();
        var theirs = Row(stranger, "theirs", DateTime.UtcNow);
        await notifications.InsertOneAsync(theirs);

        await PostReadJsonAsync(userId, $$"""{"ids":["{{theirs["_id"].AsObjectId}}"]}""");

        var reloaded = await notifications.Find(Builders<BsonDocument>.Filter.Eq("_id", theirs["_id"])).SingleAsync();
        Assert.False(reloaded.Contains("readAt"));
    }

    // ---- Upcoming reminders ------------------------------------------------

    [Fact]
    public async Task lists_only_live_unfired_reminders_inside_the_horizon()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        await tasks.InsertManyAsync(new[]
        {
            Task(userId, "Alpha", now.AddDays(20), new[]
            {
                Entry(now.AddDays(5)),
                Entry(now.AddDays(1), firedAt: now),   // already sent
                Entry(now.AddDays(40)),                // past the horizon
                Entry(now.AddHours(-1)),               // already due
            }),
            Task(userId, "Bravo", now.AddDays(2), new[] { Entry(now.AddDays(10)) }),
            Task(userId, "Charlie", null, new[] { Entry(now.AddDays(2), kind: "ai") }, status: "snoozed"),
            Task(userId, "Delta", now.AddDays(3), new[] { Entry(now.AddDays(1)) }, status: "done"),
            Task(userId, "Echo", now.AddDays(3), new[] { Entry(now.AddDays(1)) }, deletedAt: now),
        });

        var json = await GetJsonAsync(userId, "/me/reminders/upcoming");
        var reminders = json.GetProperty("reminders").EnumerateArray().ToList();

        // Ascending by `at` ACROSS tasks — Charlie (2d), Alpha (5d), Bravo (10d) —
        // which is not the dueAt order the query used.
        Assert.Equal(
            new[] { "Charlie", "Alpha", "Bravo" },
            reminders.Select(r => r.GetProperty("title").GetString()).ToArray());

        var charlie = reminders[0];
        Assert.Equal("ai", charlie.GetProperty("kind").GetString());

        // dueAt is an explicit null, never an absent key — the client branches on it.
        Assert.Equal(JsonValueKind.Null, charlie.GetProperty("dueAt").ValueKind);

        // `${taskId}:${epochMillis}`.
        var taskId = charlie.GetProperty("taskId").GetString();
        Assert.StartsWith($"{taskId}:", charlie.GetProperty("id").GetString());
    }

    // ---- helpers -----------------------------------------------------------

    private HttpClient AuthedClient(ObjectId userId)
    {
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(userId.ToString(), "d-notify@probe.com"));

        return client;
    }

    private async Task<JsonElement> GetJsonAsync(ObjectId userId, string path)
    {
        var response = await AuthedClient(userId).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadJsonAsync(response);
    }

    private Task<HttpResponseMessage> PostReadAsync(string body) =>
        AuthedClient(ObjectId.GenerateNewId()).PostAsync("/me/notifications/read", JsonBody(body));

    private async Task<JsonElement> PostReadJsonAsync(ObjectId userId, string body)
    {
        var response = await AuthedClient(userId).PostAsync("/me/notifications/read", JsonBody(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadJsonAsync(response);
    }

    private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A fresh owner plus a clean slate, so cases cannot see each other's rows.</summary>
    private static async Task<ObjectId> ResetUserAsync(IMongoCollection<BsonDocument> notifications)
    {
        var userId = ObjectId.GenerateNewId();
        await notifications.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));

        return userId;
    }

    private static BsonDocument Row(ObjectId userId, string title, DateTime createdAt, DateTime? readAt = null)
    {
        var row = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["kind"] = "reminder",
            ["title"] = title,
            ["createdAt"] = createdAt,
            ["updatedAt"] = createdAt,
            ["__v"] = 0,
        };

        if (readAt is { } read)
        {
            row["readAt"] = read;
        }

        return row;
    }

    private static BsonDocument Task(
        ObjectId userId,
        string title,
        DateTime? dueAt,
        IEnumerable<BsonDocument> reminders,
        string status = "open",
        DateTime? deletedAt = null)
    {
        var task = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["title"] = title,
            ["domain"] = "home",
            ["kind"] = "task",
            ["status"] = status,
            ["priority"] = "normal",
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(reminders),
            ["rescheduleCount"] = 0,
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
            ["__v"] = 0,
        };

        if (dueAt is { } due)
        {
            task["dueAt"] = due;
        }

        if (deletedAt is { } deleted)
        {
            task["deletedAt"] = deleted;
        }

        return task;
    }

    private static BsonDocument Entry(DateTime at, DateTime? firedAt = null, string kind = "lead")
    {
        var entry = new BsonDocument { ["at"] = at, ["kind"] = kind };
        if (firedAt is { } fired)
        {
            entry["firedAt"] = fired;
        }

        return entry;
    }

    private static IMongoCollection<BsonDocument>? TryGetNotifications() =>
        TryGetDatabase()?.GetCollection<BsonDocument>(MongoCollections.Notifications);

    private static IMongoCollection<BsonDocument>? TryGetTasks() =>
        TryGetDatabase()?.GetCollection<BsonDocument>(MongoCollections.Tasks);

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(NotificationsWebApplicationFactory.NotificationsDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
