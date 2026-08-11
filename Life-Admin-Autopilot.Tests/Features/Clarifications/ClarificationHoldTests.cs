using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot_Backend.Features.Clarifications.Binding;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Clarifications;

/// <summary>
/// <c>POST /me/clarifications</c> — the create route Node does not have.
///
/// <para>
/// <b>What it is for.</b> The planning agent runs in Langflow, outside the API, so
/// its <c>holdForClarification</c> tool can only reach the database through HTTP.
/// With no create route it could file the task and nothing else: the model answered
/// "Filed. What time is your math lecture?" and the question it had just asked was
/// stored nowhere, so no uncertainty card could ever appear and the user was asked
/// something the product could not receive an answer to.
/// </para>
///
/// <para>
/// The behaviour under test is <c>runHoldForClarification</c>
/// (<c>server/src/modules/ai/toolRunner.ts</c>), which is the semantic source of
/// truth even though the route is not. The load-bearing rule is the one about
/// <c>kind</c>: a guessed date on a high-cost item must land as a PASSIVE list entry,
/// because a reminder fired on an invented date is worse than no reminder.
/// </para>
///
/// <para>
/// Auth and validation cases touch no database and always run; the rest SKIP when the
/// parity Mongo is down, following the convention in
/// <c>ClarificationEndpointTests</c>.
/// </para>
/// </summary>
public sealed class ClarificationHoldTests : IClassFixture<ClarificationsWebApplicationFactory>
{
    private readonly ClarificationsWebApplicationFactory _factory;

    public ClarificationHoldTests(ClarificationsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Auth --------------------------------------------------------------

    [Fact]
    public async Task requires_a_token()
    {
        var response = await _factory
            .CreateApiClient()
            .PostAsync("/me/clarifications", JsonBody("""{"title":"x"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("missing_token", error.GetProperty("code").GetString());
    }

    // ---- The rule that matters --------------------------------------------

    [Fact]
    public async Task a_high_cost_guess_files_a_passive_task_and_links_the_question_to_it()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Math lecture",
          "domain": "home",
          "question": "What time is your math lecture tomorrow?",
          "kind": "date",
          "dueAtGuess": "2026-08-12T09:00:00+03:00",
          "sourceText": "Remind me that I have math lec tomorrow",
          "timezone": "Africa/Cairo"
        }
        """);

        var task = json.GetProperty("task");
        var clarification = json.GetProperty("clarification");

        // costOfWrong defaults to 'high', so the task exists but cannot fire.
        Assert.Equal("list", task.GetProperty("kind").GetString());
        Assert.Empty(task.GetProperty("reminders").EnumerateArray());
        Assert.Equal("high", clarification.GetProperty("costOfWrong").GetString());
        Assert.False(json.GetProperty("queueFull").GetBoolean());

        // ...and the guess is still recorded, on both rows: the user can see the date
        // Kitto assumed, which is what makes the question answerable.
        Assert.Equal(
            "2026-08-12T06:00:00.000Z",
            task.GetProperty("dueAt").GetString());
        Assert.Equal(
            "2026-08-12T06:00:00.000Z",
            clarification.GetProperty("draft").GetProperty("dueAt").GetString());

        // The link. `taskId` is required by the schema precisely so a held item can
        // never again exist without something the user can see and act on.
        Assert.Equal(task.GetProperty("id").GetString(), clarification.GetProperty("taskId").GetString());

        // Their own words, verbatim — the card is opened hours later, with none of
        // the conversation around it.
        Assert.Equal(
            "Remind me that I have math lec tomorrow",
            clarification.GetProperty("sourceText").GetString());

        // And it is visible on the surface the home banner reads.
        var listed = (await GetJsonAsync(userId, "/me/clarifications")).GetProperty("clarifications");
        Assert.Equal(
            clarification.GetProperty("id").GetString(),
            listed[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task a_low_cost_guess_is_allowed_to_fire_and_gets_a_real_schedule()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Water the plants",
          "domain": "home",
          "question": "Morning or evening?",
          "kind": "choice",
          "costOfWrong": "low",
          "dueAtGuess": "2027-01-15T09:00:00+02:00"
        }
        """);

        var task = json.GetProperty("task");

        // Being wrong here just means rescheduling, so the nudge is allowed through.
        Assert.Equal("reminder", task.GetProperty("kind").GetString());

        // `runCreate` plans the rules floor for a dated reminder. POST /me/tasks does
        // NOT — which is why an agent that created the task itself left this empty.
        Assert.NotEmpty(task.GetProperty("reminders").EnumerateArray());
    }

    [Fact]
    public async Task with_no_guess_at_all_the_task_is_dateless_and_still_created()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Email that guy",
          "domain": "home",
          "question": "Which guy, and what about?",
          "kind": "detail"
        }
        """);

        // Withholding the task is what this whole design stopped doing: a captured
        // thought used to be invisible until answered — not in Matters, not
        // searchable, not deletable.
        var task = json.GetProperty("task");
        Assert.Equal("list", task.GetProperty("kind").GetString());
        Assert.False(task.TryGetProperty("dueAt", out _));
        Assert.Equal("open", json.GetProperty("clarification").GetProperty("status").GetString());
    }

    [Fact]
    public async Task the_first_option_is_the_guess_when_no_explicit_one_is_given()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // The model orders its options most-likely first, so option zero IS the guess.
        var json = await PostCreatedAsync(userId, """
        {
          "title": "Renew the passport",
          "domain": "home",
          "question": "Is it the 15th or the 18th?",
          "kind": "date",
          "options": [
            {"label": "The 15th", "dueAt": "2026-09-15T10:30:00Z", "title": "Renew the passport (15th)"},
            {"label": "The 18th", "dueAt": "2026-09-18T10:30:00Z", "notes": "Second slot"}
          ]
        }
        """);

        Assert.Equal("2026-09-15T10:30:00.000Z", json.GetProperty("task").GetProperty("dueAt").GetString());

        var options = json.GetProperty("clarification").GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("The 15th", options[0].GetProperty("label").GetString());
        Assert.Equal("Renew the passport (15th)", options[0].GetProperty("title").GetString());

        // An option with no title/notes omits the keys entirely rather than sending
        // nulls — Mongoose never stores an unset optional.
        Assert.False(options[1].TryGetProperty("title", out _));
        Assert.Equal("Second slot", options[1].GetProperty("notes").GetString());
    }

    [Fact]
    public async Task a_naive_guess_is_read_in_the_callers_zone_not_as_utc()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // The model routinely drops the offset. Reading that as UTC puts a Cairo
        // user's 9am reminder at noon, with nothing reporting an error.
        var json = await PostCreatedAsync(userId, """
        {
          "title": "Dentist",
          "domain": "health",
          "question": "Which day?",
          "kind": "date",
          "costOfWrong": "low",
          "dueAtGuess": "2026-08-12T09:00:00",
          "timezone": "Africa/Cairo"
        }
        """);

        Assert.Equal("2026-08-12T06:00:00.000Z", json.GetProperty("task").GetProperty("dueAt").GetString());
    }

    // ---- What the row actually looks like on disk --------------------------

    [Fact]
    public async Task the_stored_row_carries_no_source_key_and_is_stamped_like_mongoose()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Car service",
          "domain": "car",
          "question": "Which garage?",
          "kind": "choice",
          "tags": ["Car Service", "car service", ""],
          "notes": "the usual place"
        }
        """);

        var stored = await Clarifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id",
                ObjectId.Parse(json.GetProperty("clarification").GetProperty("id").GetString())))
            .FirstAsync();

        // sourceKey is the VOICE lane's note-scoped idempotency key. A chat-born hold
        // has none, and the partial unique index is keyed on its PRESENCE — writing a
        // null here would be a different document from the one the reference stores.
        Assert.False(stored.Contains("sourceKey"));

        // Mongoose stamps these; the .NET driver stamps nothing, so the port must.
        Assert.Equal(0, stored["__v"].AsInt32);
        Assert.Equal(stored["createdAt"].ToUniversalTime(), stored["updatedAt"].ToUniversalTime());

        // Tags are folded to lowercase-kebab and de-duplicated, exactly as on the
        // Matters create path — the draft has to round-trip into a real task.
        Assert.Equal(new[] { "car-service" }, stored["draft"]["tags"].AsBsonArray.Select(t => t.AsString).ToArray());

        // sourceKey never reaches a client either.
        Assert.False(json.GetProperty("clarification").TryGetProperty("sourceKey", out _));
    }

    [Fact]
    public async Task an_over_long_quote_is_clamped_rather_than_stored_whole()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, $$"""
        {
          "title": "Long one",
          "domain": "home",
          "question": "Which?",
          "kind": "detail",
          "sourceText": "{{new string('a', 1_500)}}"
        }
        """);

        var quote = json.GetProperty("clarification").GetProperty("sourceText").GetString()!;
        Assert.Equal(SourceQuote.MaxSourceText, quote.Length);
        Assert.EndsWith("…", quote, StringComparison.Ordinal);
    }

    // ---- The queue cap -----------------------------------------------------

    [Fact]
    public async Task past_the_cap_the_task_is_still_filed_and_the_question_is_not()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // A chat-born hold carries no sourceKey, so nothing makes it idempotent: the
        // same fuzzy item could be held again every turn, forever. The cap is the
        // backpressure — and it counts DEFERRED questions too, because a skipped one
        // is still queued and still comes back.
        await Clarifications(db).InsertManyAsync(
            Enumerable.Range(0, ClarificationHoldService.MaxOpenClarifications)
                .Select(i => Existing(userId, i, deferred: i % 2 == 0)));

        var json = await PostCreatedAsync(userId, """
        {
          "title": "One too many",
          "domain": "home",
          "question": "Will this be asked?",
          "kind": "detail"
        }
        """);

        // A slightly-wrong but VISIBLE task beats a question the user never reaches.
        Assert.True(json.GetProperty("queueFull").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("clarification").ValueKind);
        Assert.Equal("One too many", json.GetProperty("task").GetProperty("title").GetString());

        Assert.Equal(
            (long)ClarificationHoldService.MaxOpenClarifications,
            await Clarifications(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    // ---- Validation --------------------------------------------------------

    [Fact]
    public async Task the_four_required_fields_are_reported_together()
    {
        var response = await PostAsync(ObjectId.GenerateNewId(), """{}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal("Invalid clarification payload.", error.GetProperty("message").GetString());

        // zod reports every issue in ONE response — never the first one only.
        var fields = error.GetProperty("details").GetProperty("fieldErrors");
        foreach (var name in new[] { "title", "domain", "question", "kind" })
        {
            Assert.Equal("Required", fields.GetProperty(name)[0].GetString());
        }
    }

    [Fact]
    public async Task a_loose_date_is_rejected_with_the_schemas_own_words()
    {
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "Q", "kind": "date",
          "dueAtGuess": "2026-08-12"
        }
        """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // STRICT_DATETIME_RE rejects date-only, space-separated, and anything looser.
        var fields = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details")
            .GetProperty("fieldErrors");
        Assert.Equal(HoldBinder.NotIsoDatetime, fields.GetProperty("dueAtGuess")[0].GetString());
    }

    [Fact]
    public async Task a_fifth_option_is_rejected_as_an_array_not_a_string()
    {
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "Q", "kind": "date",
          "options": [{"label":"a"},{"label":"b"},{"label":"c"},{"label":"d"},{"label":"e"}]
        }
        """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // "element(s)", not "character(s)" — the array and string forms differ by one
        // word and are easy to cross-wire.
        var fields = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details")
            .GetProperty("fieldErrors");
        Assert.Equal("Array must contain at most 4 element(s)", fields.GetProperty("options")[0].GetString());
    }

    [Fact]
    public async Task an_unresolvable_timezone_is_a_400_not_a_500()
    {
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "Q", "kind": "date",
          "timezone": "Mars/Olympus_Mons"
        }
        """);

        // The value arrives from a language model. Node's Intl would throw an
        // uncaught RangeError and answer 500; turning a modelling error into a server
        // error teaches the agent nothing it can act on.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var fields = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details")
            .GetProperty("fieldErrors");
        Assert.Equal(HoldBinder.UnknownTimezone, fields.GetProperty("timezone")[0].GetString());
    }

    [Fact]
    public async Task an_unknown_key_is_stripped_rather_than_rejected()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // holdForClarificationArgs is a plain z.object, NOT .strict() — so unknown
        // keys are silently dropped. Only the me.tasks schemas reject them.
        var response = await PostAsync(userId, """
        {
          "title": "T", "domain": "home", "question": "Q", "kind": "date",
          "bogus": "ignore me"
        }
        """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- The date normaliser, in isolation ---------------------------------

    [Theory]
    // An explicit offset is unambiguous and is taken at face value.
    [InlineData("2026-08-12T09:00:00+03:00", "Africa/Cairo", "2026-08-12T06:00:00.000Z")]
    [InlineData("2026-08-12T09:00:00Z", "Africa/Cairo", "2026-08-12T09:00:00.000Z")]
    // Naive: the wall clock the user meant, in the zone they are in.
    [InlineData("2026-08-12T09:00:00", "Africa/Cairo", "2026-08-12T06:00:00.000Z")]
    [InlineData("2026-08-12T09:00", "America/New_York", "2026-08-12T13:00:00.000Z")]
    // Naive with no zone: UTC, deliberately — never the host's local time, which
    // would make the answer depend on where the server happens to run.
    [InlineData("2026-08-12T09:00:00", null, "2026-08-12T09:00:00.000Z")]
    // `Date.UTC(y,m,d,h,mi,s)` never receives the fractional part, so a naive value
    // loses it. Reproduced, not corrected.
    [InlineData("2026-08-12T09:00:00.750", "Africa/Cairo", "2026-08-12T06:00:00.000Z")]
    public void naive_and_offset_dates_normalise_the_way_the_reference_does(
        string iso,
        string? timezone,
        string expected)
    {
        Assert.Equal(
            expected,
            HoldTimeNormalizer.Normalize(iso, timezone).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }

    [Theory]
    [InlineData("2026-08-12")]
    [InlineData("2026-08-12 09:00:00")]
    [InlineData("tomorrow at nine")]
    [InlineData("2026-08-12T09:00:00+0300")]
    [InlineData("2026-08-12T09:00:00\n")]
    public void the_strict_pattern_rejects_everything_looser(string iso)
    {
        // The trailing-newline case is why the pattern is anchored \A..\z: .NET's `$`
        // also matches before a final newline, and JavaScript's does not.
        Assert.False(HoldTimeNormalizer.IsStrictIso(iso));
    }

    // ---- Helpers -----------------------------------------------------------

    private HttpClient AuthedClient(ObjectId userId)
    {
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(userId.ToString(), "h-hold@probe.com"));
        return client;
    }

    private Task<HttpResponseMessage> PostAsync(ObjectId userId, string body) =>
        AuthedClient(userId).PostAsync("/me/clarifications", JsonBody(body));

    private async Task<JsonElement> PostCreatedAsync(ObjectId userId, string body)
    {
        var response = await PostAsync(userId, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<JsonElement> GetJsonAsync(ObjectId userId, string path)
    {
        var response = await AuthedClient(userId).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<ObjectId> ResetAsync(IMongoDatabase db)
    {
        var userId = ObjectId.GenerateNewId();
        await Clarifications(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));
        await Tasks(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));
        return userId;
    }

    /// <summary>An already-open question, for filling the queue up to the cap.</summary>
    private static BsonDocument Existing(ObjectId userId, int index, bool deferred)
    {
        var at = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(index);

        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["taskId"] = ObjectId.GenerateNewId(),
            ["status"] = "open",
            ["draft"] = new BsonDocument
            {
                ["title"] = $"Held {index}",
                ["domain"] = "home",
                ["priority"] = "normal",
                ["tags"] = new BsonArray(),
            },
            ["question"] = $"Question {index}?",
            ["kind"] = "date",
            ["costOfWrong"] = "high",
            ["options"] = new BsonArray(),
            ["createdAt"] = at,
            ["updatedAt"] = at,
            ["__v"] = 0,
        };

        if (deferred)
        {
            doc["deferredUntil"] = at.AddYears(50);
        }

        return doc;
    }

    private static IMongoCollection<BsonDocument> Clarifications(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>(MongoCollections.Clarifications);

    private static IMongoCollection<BsonDocument> Tasks(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>(MongoCollections.Tasks);

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(ClarificationsWebApplicationFactory.ClarificationsDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
