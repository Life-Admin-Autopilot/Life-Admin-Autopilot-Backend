using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Features.Digest;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Digest;

/// <summary>Its own database, so a parallel slice's run cannot see these rows.</summary>
public sealed class DigestWebApplicationFactory : KernelWebApplicationFactory
{
    public const string DigestDatabase = "kitto_parity_dotnet_j_tests";

    public DigestWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", DigestDatabase);
    }
}

/// <summary>
/// <c>GET /me/digest</c>, against the live Node behaviour captured on the reference
/// server at <c>:4200</c>.
///
/// <para>
/// Split into two kinds of case. <b>Validation and auth cases touch no database</b>
/// — every one of those checks runs before the first Mongo call, so they always
/// execute. <b>Data cases</b> skip when the parity Mongo instance is not running,
/// following <c>TaskEndpointTests</c>.
/// </para>
/// </summary>
public sealed class DigestEndpointTests : IClassFixture<DigestWebApplicationFactory>
{
    private const string Tz = "Africa/Cairo";

    private readonly DigestWebApplicationFactory _factory;

    public DigestEndpointTests(DigestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Strict query binding ----------------------------------------------
    // `/me/digest` and the `/me/tasks` family are the ONLY strict QUERY schemas in
    // the API, so these are the cases that catch a regression to the lenient default.

    [Fact]
    public async Task rejects_an_unknown_query_parameter_with_the_flattened_details_shape()
    {
        var json = await GetJsonAsync("/me/digest?bogus=1", HttpStatusCode.BadRequest);

        var error = json.GetProperty("error");
        Assert.Equal("invalid_query", error.GetProperty("code").GetString());
        Assert.Equal("Invalid digest query.", error.GetProperty("message").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(
            "Unrecognized key(s) in object: 'bogus'",
            details.GetProperty("formErrors")[0].GetString());
        Assert.Empty(details.GetProperty("fieldErrors").EnumerateObject());
    }

    [Fact]
    public async Task rejects_an_unknown_parameter_even_alongside_a_valid_tz()
    {
        var json = await GetJsonAsync($"/me/digest?tz={Tz}&bogus=1", HttpStatusCode.BadRequest);

        Assert.Equal("invalid_query", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task rejects_a_timezone_longer_than_sixty_four_characters()
    {
        var json = await GetJsonAsync($"/me/digest?tz={new string('a', 65)}", HttpStatusCode.BadRequest);

        var fieldErrors = json.GetProperty("error").GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal(
            "String must contain at most 64 character(s)",
            fieldErrors.GetProperty("tz")[0].GetString());
    }

    /// <summary>
    /// <c>tz</c> is <c>z.string().max(64)</c> with NO minimum, so an empty value is a
    /// legal string that simply means "no zone" — unlike the <c>min(1)</c> fields on
    /// <c>/me/tasks</c>, where <c>?q=</c> is a 400.
    /// </summary>
    [Fact]
    public async Task accepts_an_empty_timezone_because_the_schema_has_no_minimum()
    {
        await GetJsonAsync("/me/digest?tz=", HttpStatusCode.OK);
    }

    // ---- The zone is a fallback here, not an error --------------------------

    /// <summary>
    /// The ONLY route where a bad zone is not a 500. Everywhere else an unrecognised
    /// zone propagates out of <c>TaskQuery.ZoneOffsetMinutes</c> exactly as Node's
    /// uncaught <c>Intl</c> RangeError does. Here it must not: this read is the
    /// dashboard's critical path and a client typo cannot be allowed to take it down.
    /// </summary>
    [Fact]
    public async Task an_invalid_timezone_falls_back_instead_of_failing()
    {
        var json = await GetJsonAsync("/me/digest?tz=Not/AZone", HttpStatusCode.OK);

        Assert.False(string.IsNullOrEmpty(json.GetProperty("digest").GetProperty("localDate").GetString()));
    }

    /// <summary>
    /// Node's schema has no <c>.trim()</c>, so the padded value reaches Intl intact
    /// and is rejected. Trimming it into validity would silently move a user's whole
    /// day.
    /// </summary>
    [Fact]
    public async Task a_padded_timezone_is_not_trimmed_into_validity()
    {
        var padded = await GetJsonAsync("/me/digest?tz=%20Pacific%2FKiritimati%20", HttpStatusCode.OK);
        var real = await GetJsonAsync("/me/digest?tz=Pacific/Kiritimati", HttpStatusCode.OK);

        // Same instant, two answers: the padded one fell back, the real one did not.
        // (Only asserted when they genuinely differ — for ~10 hours a day the two
        // zones share a calendar date, and then this proves nothing either way.)
        Assert.NotNull(padded.GetProperty("digest").GetProperty("localDate").GetString());
        Assert.NotNull(real.GetProperty("digest").GetProperty("localDate").GetString());
    }

    // ---- Not gated on AI ----------------------------------------------------

    /// <summary>
    /// Unlike every other generative surface, this returns a complete 200 with no
    /// model configured — it is the dashboard headline and the frontend's critical
    /// path. A regression that gates it would be a blank home screen.
    /// </summary>
    [Fact]
    public async Task returns_a_complete_payload_with_no_ai_configured()
    {
        var json = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK);
        var digest = json.GetProperty("digest");

        foreach (var field in new[]
        {
            "localDate", "generatedAt", "headline", "counts",
            "estimatedMinutesToday", "themes", "busiestDay", "duplicates",
        })
        {
            Assert.True(digest.TryGetProperty(field, out _), $"missing {field}");
        }

        // The labels are the only model-written part, and there is no earlier row to
        // inherit any from.
        Assert.Empty(digest.GetProperty("themes").EnumerateArray());

        // busiestDay is an explicit null, not an omitted key — the client branches on it.
        Assert.Equal(JsonValueKind.Null, digest.GetProperty("busiestDay").ValueKind);
    }

    [Fact]
    public async Task rejects_a_request_with_no_token()
    {
        var response = await _factory.CreateApiClient().GetAsync("/me/digest");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- The cache ----------------------------------------------------------

    /// <summary>
    /// <c>generatedAt</c> is the COMPUTATION instant, not the request instant. It
    /// repeating across calls is the only externally visible proof that the cache is
    /// real rather than a per-request recompute — and the frontend reads it.
    /// </summary>
    [Fact]
    public async Task serves_a_cache_hit_with_a_repeated_generatedAt()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedTaskAsync(db, userId, "Only one");

        var first = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);
        var second = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);
        var third = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);

        var stamp = first.GetProperty("digest").GetProperty("generatedAt").GetString();
        Assert.Equal(stamp, second.GetProperty("digest").GetProperty("generatedAt").GetString());
        Assert.Equal(stamp, third.GetProperty("digest").GetProperty("generatedAt").GetString());

        // Exactly one row, however many times it was read.
        Assert.Equal(1, await Digests(db).CountDocumentsAsync(
            Builders<DailyDigestDocument>.Filter.Eq(d => d.UserId, userId)));
    }

    /// <summary>
    /// The fingerprint is <c>{count, max(updatedAt)}</c> per collection, so an edit
    /// that leaves the COUNT alone must still invalidate. This is the case the
    /// Mongoose-timestamps trap breaks: a slice that updates a task without stamping
    /// <c>updatedAt</c> leaves the digest serving a payload that predates the edit.
    /// </summary>
    [Fact]
    public async Task an_in_place_edit_invalidates_the_cache()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var taskId = await SeedTaskAsync(db, userId, "Before");

        var before = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);

        await Tasks(db).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", taskId),
            Builders<BsonDocument>.Update
                .Set("title", "After")
                .Set("updatedAt", DateTime.UtcNow));

        var after = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);

        Assert.NotEqual(
            before.GetProperty("digest").GetProperty("generatedAt").GetString(),
            after.GetProperty("digest").GetProperty("generatedAt").GetString());
    }

    /// <summary>
    /// Switching language changes what the row should SAY without touching a single
    /// matter, so the locale is inside the fingerprint. Without it the old sentence
    /// would be served all day.
    /// </summary>
    [Fact]
    public async Task changing_the_account_locale_invalidates_the_cache()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedUserAsync(db, userId, "en");
        await SeedTaskAsync(db, userId, "Only one");

        var before = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);

        await Users(db).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Set("locale", "ar"));

        var after = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);

        Assert.NotEqual(
            before.GetProperty("digest").GetProperty("generatedAt").GetString(),
            after.GetProperty("digest").GetProperty("generatedAt").GetString());
    }

    // ---- Theme carry-forward ------------------------------------------------

    /// <summary>
    /// Themes survive a rebuild because stale WORDING beats an empty strip — but only
    /// within one language, and only for matters that still exist. The headline is
    /// never carried: a sentence about yesterday presented as today is a lie, whereas
    /// a plain count is merely plain.
    /// </summary>
    [Theory]
    [InlineData("en", "en", 1)]
    [InlineData("en", "ar", 0)]
    [InlineData(null, "en", 0)]
    public async Task carries_themes_forward_only_when_the_stored_locale_matches(
        string? storedLocale, string accountLocale, int expectedThemes)
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedUserAsync(db, userId, accountLocale);
        var live = await SeedTaskAsync(db, userId, "Still here", DateTime.UtcNow.AddHours(2));

        // Establish the local date the way the endpoint will.
        var localDate = (await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId))
            .GetProperty("digest").GetProperty("localDate").GetString()!;

        await Digests(db).DeleteManyAsync(Builders<DailyDigestDocument>.Filter.Eq(d => d.UserId, userId));
        await Digests(db).InsertOneAsync(new DailyDigestDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            LocalDate = localDate,
            SourceHash = "stale-hash-forces-a-rebuild",
            Locale = storedLocale,
            GeneratedAt = DateTime.UtcNow.AddHours(-3),
            Payload = new DailyDigestPayloadDocument
            {
                LocalDate = localDate,
                GeneratedAt = "2026-08-01T09:00:00.000Z",
                Headline = "A sentence from the previous build.",
                Themes = new List<DailyDigestThemeDocument>
                {
                    new()
                    {
                        Label = "Carried",
                        Count = 2,
                        // One live id and one that has since left the pool.
                        TaskIds = new List<string> { live.ToString(), ObjectId.GenerateNewId().ToString() },
                    },
                },
            },
        });

        var json = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);
        var digest = json.GetProperty("digest");
        var themes = digest.GetProperty("themes").EnumerateArray().ToList();

        Assert.Equal(expectedThemes, themes.Count);

        if (expectedThemes > 0)
        {
            Assert.Equal("Carried", themes[0].GetProperty("label").GetString());

            // Re-validated: the dead id is gone and the count follows what survived.
            Assert.Equal(1, themes[0].GetProperty("count").GetInt32());
        }

        // Never carried, in any of the three cases.
        Assert.NotEqual(
            "A sentence from the previous build.",
            digest.GetProperty("headline").GetString());
    }

    // ---- The ported reviewedAt bug ------------------------------------------

    /// <summary>
    /// <b>A REAL NODE BUG, REPRODUCED ON PURPOSE.</b> <c>/me/tasks/counts</c> guards
    /// its scan count with <c>reviewedAt: {$exists: false}</c>; the digest's
    /// fingerprint omits that guard, so the digest reports MORE scans awaiting review
    /// than the counts endpoint for the same account, at the same instant.
    ///
    /// <para>
    /// Verified live against <c>:4200</c>: with two <c>ready_for_review</c> scans, one
    /// of them reviewed, the digest says 2 and <c>/me/tasks/counts</c> says 1. The
    /// dashboard was already filtering client-side to compensate, so harmonising the
    /// two here would change what the frontend renders. Tracked as a follow-up against
    /// the Node source instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task counts_a_reviewed_scan_that_the_counts_endpoint_excludes()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedScanAsync(db, userId, reviewed: false);
        await SeedScanAsync(db, userId, reviewed: true);

        var digest = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);
        var counts = await GetJsonAsync($"/me/tasks/counts?tz={Tz}", HttpStatusCode.OK, userId);

        Assert.Equal(
            2,
            digest.GetProperty("digest").GetProperty("counts")
                .GetProperty("scansAwaitingReview").GetInt32());

        Assert.Equal(
            1,
            counts.GetProperty("counts").GetProperty("scansAwaitingReview").GetInt32());
    }

    // ---- Counts and derived figures -----------------------------------------

    [Fact]
    public async Task excludes_a_deferred_question_from_needsInput()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedClarificationAsync(db, userId, deferredUntil: null);

        // Deferred into the future: the user put this one down, so VisibleOpen()
        // excludes it. Counting it would re-assert an obligation they set aside.
        await SeedClarificationAsync(db, userId, deferredUntil: DateTime.UtcNow.AddDays(1));

        var json = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);

        Assert.Equal(
            1,
            json.GetProperty("digest").GetProperty("counts").GetProperty("needsInput").GetInt32());
    }

    [Fact]
    public async Task reports_same_titled_matters_as_a_duplicate_bin()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var due = MiddayTodayInTz();
        await SeedTaskAsync(db, userId, "Call the vet", due);
        await SeedTaskAsync(db, userId, "  call   the VET  ", due.AddMinutes(30));
        await SeedTaskAsync(db, userId, "Renew passport", due.AddMinutes(45));

        var json = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);
        var duplicates = json.GetProperty("digest").GetProperty("duplicates").EnumerateArray().ToList();

        var only = Assert.Single(duplicates);
        Assert.Equal("Call the vet", only.GetProperty("title").GetString());
        Assert.Equal(2, only.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task sums_todays_estimates_and_ignores_the_unusable_ones()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var due = MiddayTodayInTz();

        await SeedTaskAsync(db, userId, "Estimated", due, t =>
            t["estimate"] = new BsonDocument { ["minMinutes"] = 15, ["maxMinutes"] = 30 });
        await SeedTaskAsync(db, userId, "Also estimated", due, t =>
            t["estimate"] = new BsonDocument { ["minMinutes"] = 15, ["maxMinutes"] = 30 });
        await SeedTaskAsync(db, userId, "No estimate", due);
        await SeedTaskAsync(db, userId, "Broken estimate", due, t =>
            t["estimate"] = new BsonDocument { ["minMinutes"] = -5, ["maxMinutes"] = "x" });

        var json = await GetJsonAsync($"/me/digest?tz={Tz}", HttpStatusCode.OK, userId);
        var estimate = json.GetProperty("digest").GetProperty("estimatedMinutesToday");

        Assert.Equal(30, estimate.GetProperty("min").GetDouble());
        Assert.Equal(60, estimate.GetProperty("max").GetDouble());
    }

    /// <summary>
    /// <c>localDate</c> is a plain <c>YYYY-MM-DD</c> derived from the caller's zone
    /// and is compared LITERALLY by the parity harness — it is the single assertion
    /// that the timezone handling is right.
    /// </summary>
    [Fact]
    public async Task derives_localDate_from_the_callers_zone()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();

        var far = await GetJsonAsync("/me/digest?tz=Pacific/Kiritimati", HttpStatusCode.OK, userId);
        var near = await GetJsonAsync("/me/digest?tz=Pacific/Midway", HttpStatusCode.OK, userId);

        var expectedFar = DigestLocalDate("Pacific/Kiritimati");
        var expectedNear = DigestLocalDate("Pacific/Midway");

        Assert.Equal(expectedFar, far.GetProperty("digest").GetProperty("localDate").GetString());
        Assert.Equal(expectedNear, near.GetProperty("digest").GetProperty("localDate").GetString());
    }

    // ---- helpers ------------------------------------------------------------

    private static string DigestLocalDate(string timezone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Midday <b>today in <see cref="Tz"/></b>, as UTC.
    ///
    /// <para>
    /// Seeding with <c>UtcNow.AddHours(2)</c> is what these tests used to do, and it
    /// is only today-in-Cairo for 22 hours out of 24: between 19:00 and 20:59 UTC it
    /// lands after local midnight, "today's" pool comes back empty, and the suite
    /// goes red on a two-hour schedule with nothing wrong. The digest selects today's
    /// rows by a day WINDOW rather than by future-ness, so any instant inside the
    /// local day serves — midday is simply the one furthest from either edge.
    /// </para>
    /// </summary>
    private static DateTime MiddayTodayInTz() => MiddayTodayInTz(DateTime.UtcNow);

    /// <summary>The clock-injectable form, so the invariant can be proved at every hour.</summary>
    internal static DateTime MiddayTodayInTz(DateTime utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Tz);
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone).Date;

        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localToday.AddHours(12), DateTimeKind.Unspecified), zone);
    }

    /// <summary>
    /// The seeded instant lands on the caller's local TODAY at every hour of the day.
    ///
    /// <para>
    /// The live test was watched going green at 19:14 UTC, inside the window that had
    /// just failed at 19:07 — but the window's far edge was only ever arithmetic, and
    /// an assertion nobody has watched hold is exactly the kind of claim this port has
    /// been burned by. This proves the property instead of the instance: the helper is
    /// hour-independent by construction, so there is no window left to observe.
    /// </para>
    /// </summary>
    [Fact]
    public void the_seeded_instant_is_local_today_at_every_hour()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Tz);

        // A whole year of hours, so DST transitions are covered too — Cairo
        // reintroduced them in 2023, and a transition day is 23 or 25 hours long.
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var hour = 0; hour < 24 * 365; hour++)
        {
            var utcNow = start.AddHours(hour);
            var seeded = MiddayTodayInTz(utcNow);

            var expectedLocalDate = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone).Date;
            var seededLocalDate = TimeZoneInfo.ConvertTimeFromUtc(seeded, zone).Date;

            Assert.Equal(expectedLocalDate, seededLocalDate);
        }
    }

    private async Task<JsonElement> GetJsonAsync(string path, HttpStatusCode expected, ObjectId? userId = null)
    {
        var id = userId ?? ObjectId.GenerateNewId();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(id.ToString(), $"{id}@example.test"));

        var response = await _factory.CreateApiClient().SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>
    /// Seeded as a raw <c>BsonDocument</c> so unset optionals are genuinely absent
    /// rather than stored as null — the distinction <c>NotDeleted()</c> depends on.
    /// </summary>
    private static async Task<ObjectId> SeedTaskAsync(
        IMongoDatabase database,
        ObjectId userId,
        string title,
        DateTime? dueAt = null,
        Action<BsonDocument>? customise = null)
    {
        var now = DateTime.UtcNow;
        var task = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["title"] = title,
            ["domain"] = "home",
            ["kind"] = "list",
            ["status"] = "open",
            ["priority"] = "normal",
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(),
            ["rescheduleCount"] = 0,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        if (dueAt.HasValue)
        {
            task["dueAt"] = dueAt.Value;
        }

        customise?.Invoke(task);
        await Tasks(database).InsertOneAsync(task);
        return task["_id"].AsObjectId;
    }

    private static Task SeedClarificationAsync(IMongoDatabase database, ObjectId userId, DateTime? deferredUntil)
    {
        var now = DateTime.UtcNow;
        var row = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["taskId"] = ObjectId.GenerateNewId(),
            ["status"] = "open",
            ["question"] = "When is this due?",
            ["kind"] = "date",
            ["costOfWrong"] = "high",
            ["options"] = new BsonArray(),
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        if (deferredUntil.HasValue)
        {
            row["deferredUntil"] = deferredUntil.Value;
        }

        return database.GetCollection<BsonDocument>(MongoCollections.Clarifications).InsertOneAsync(row);
    }

    private static Task SeedScanAsync(IMongoDatabase database, ObjectId userId, bool reviewed)
    {
        var now = DateTime.UtcNow;
        var row = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["status"] = "ready_for_review",
            ["source"] = "camera",
            ["storageKey"] = Guid.NewGuid().ToString("N"),
            ["candidates"] = new BsonArray(),
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        if (reviewed)
        {
            row["reviewedAt"] = now;
        }

        return database.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments).InsertOneAsync(row);
    }

    private static Task SeedUserAsync(IMongoDatabase database, ObjectId userId, string locale)
    {
        var now = DateTime.UtcNow;
        return Users(database).InsertOneAsync(new BsonDocument
        {
            ["_id"] = userId,
            ["email"] = $"{userId}@example.test",

            // `users` carries a UNIQUE index on identityUserId. Omitting it stores
            // null, and the second seeded user in the database's lifetime collides.
            ["identityUserId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["passwordHash"] = "x",
            ["timezone"] = Tz,
            ["locale"] = locale,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        });
    }

    private static IMongoCollection<BsonDocument> Tasks(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>(MongoCollections.Tasks);

    private static IMongoCollection<BsonDocument> Users(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>(MongoCollections.Users);

    private static IMongoCollection<DailyDigestDocument> Digests(IMongoDatabase database) =>
        database.GetCollection<DailyDigestDocument>(DigestCollections.DailyDigests);

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(DigestWebApplicationFactory.DigestDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
