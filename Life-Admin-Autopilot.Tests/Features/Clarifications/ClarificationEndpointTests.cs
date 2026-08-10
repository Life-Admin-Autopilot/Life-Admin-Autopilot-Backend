using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Clarifications;

/// <summary>
/// Its own database, so a parallel slice's test run cannot see these rows.
/// </summary>
public sealed class ClarificationsWebApplicationFactory : KernelWebApplicationFactory
{
    public const string ClarificationsDatabase = "kitto_parity_dotnet_h_tests";

    public ClarificationsWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", ClarificationsDatabase);
    }
}

/// <summary>
/// The four <c>/me/clarifications</c> endpoints, against behaviour captured live on
/// the reference at <c>:4200</c> with a seeded differential.
///
/// <para>
/// <b>Why everything here is seeded directly into Mongo.</b> With no
/// <c>GEMINI_API_KEY</c> a clarification can never be CREATED through the API — rows
/// are written only by the AI tool runner and the voice transcriber, both hard-gated
/// on <c>isAiConfigured()</c>. So the list is provably always empty on a parity
/// server and every interesting branch is unreachable from the outside. Seeding is
/// the only way to exercise them.
/// </para>
///
/// <para>
/// Auth cases touch no database and always run; data cases SKIP (rather than fail)
/// when the parity Mongo is not up, following <c>UsageQuotaTests.TryCreateStore</c>.
/// </para>
/// </summary>
public sealed class ClarificationEndpointTests : IClassFixture<ClarificationsWebApplicationFactory>
{
    private readonly ClarificationsWebApplicationFactory _factory;

    public ClarificationEndpointTests(ClarificationsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static readonly DateTime Epoch = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    // ---- Auth --------------------------------------------------------------

    [Fact]
    public async Task requires_a_token_to_list()
    {
        var response = await _factory.CreateApiClient().GetAsync("/me/clarifications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("missing_token", error.GetProperty("code").GetString());
        Assert.Equal("Missing access token", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("resolve")]
    [InlineData("defer")]
    [InlineData("drop")]
    public async Task requires_a_token_on_every_write(string action)
    {
        var response = await _factory
            .CreateApiClient()
            .PostAsync($"/me/clarifications/{ObjectId.GenerateNewId()}/{action}", JsonBody("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- The list ----------------------------------------------------------

    [Fact]
    public async Task lists_only_visible_open_questions_newest_first()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // The predicate under test is VisibleOpen(): status 'open' AND (deferredUntil
        // absent OR already passed). Every surface that counts or lists held items
        // composes it — the dashboard and the digest disagreed once for exactly this
        // reason, which is why it lives on the repository base.
        await Clarifications(db).InsertManyAsync(new[]
        {
            Clar(userId, "newest open", Epoch.AddMinutes(5)),
            Clar(userId, "deferred into the past", Epoch.AddMinutes(4), deferredUntil: Epoch.AddYears(-1)),
            Clar(userId, "resolved", Epoch.AddMinutes(3), status: "resolved"),
            Clar(userId, "dropped", Epoch.AddMinutes(2), status: "dropped"),
            Clar(userId, "deferred into the future", Epoch.AddMinutes(1), deferredUntil: Epoch.AddYears(50)),
            Clar(userId, "oldest open", Epoch),
        });

        var json = await GetJsonAsync(userId, "/me/clarifications");

        Assert.Equal(
            new[] { "newest open", "deferred into the past", "oldest open" },
            json.GetProperty("clarifications").EnumerateArray()
                .Select(c => c.GetProperty("question").GetString())
                .ToArray());

        Assert.False(json.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task never_emits_the_internal_source_key()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // sourceKey is the voice-born idempotency key. The model's toJSON DELETES it;
        // it must never reach a client.
        var row = Clar(userId, "voice born", Epoch);
        row["sourceKey"] = "note:abc:item:1";
        await Clarifications(db).InsertOneAsync(row);

        var item = (await GetJsonAsync(userId, "/me/clarifications")).GetProperty("clarifications")[0];

        Assert.False(item.TryGetProperty("sourceKey", out _));
        Assert.Equal("voice born", item.GetProperty("question").GetString());
    }

    [Fact]
    public async Task pages_at_fifty_with_a_cursor_on_created_at()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // 51 rows: the route fetches PAGE_SIZE + 1 so it can answer hasMore honestly
        // rather than silently truncating at a round number.
        await Clarifications(db).InsertManyAsync(
            Enumerable.Range(0, 51).Select(i => Clar(userId, $"Q{i}", Epoch.AddMinutes(i))));

        var first = await GetJsonAsync(userId, "/me/clarifications");

        var page = first.GetProperty("clarifications").EnumerateArray().ToList();
        Assert.Equal(50, page.Count);
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal("Q50", page[0].GetProperty("question").GetString());
        Assert.Equal("Q1", page[^1].GetProperty("question").GetString());

        // nextCursor is the LAST item's createdAt, so the next page is strictly older.
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.Equal(page[^1].GetProperty("createdAt").GetString(), cursor);

        var second = await GetJsonAsync(userId, $"/me/clarifications?before={Uri.EscapeDataString(cursor!)}");

        var tail = second.GetProperty("clarifications").EnumerateArray().ToList();
        Assert.Single(tail);
        Assert.Equal("Q0", tail[0].GetProperty("question").GetString());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("?before=garbage")]
    [InlineData("?before=")]
    [InlineData("?bogus=1")]
    [InlineData("?before=2026-03-01T09:00:00Z&before=2026-03-01T09:05:00Z")]
    public async Task ignores_an_unusable_query_rather_than_rejecting_it(string query)
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        await Clarifications(db).InsertOneAsync(Clar(userId, "still here", Epoch));

        // There is NO query schema on this route, so an unparsable cursor is dropped
        // and the first page comes back — never a 400. A REPEATED parameter is an
        // array in express, which fails the `typeof === 'string'` test, so it is
        // ignored too. Both verified live.
        var json = await GetJsonAsync(userId, $"/me/clarifications{query}");

        Assert.Single(json.GetProperty("clarifications").EnumerateArray());
    }

    [Fact]
    public async Task never_leaks_another_accounts_questions()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var stranger = ObjectId.GenerateNewId();
        var theirs = Clar(stranger, "not yours", Epoch);
        await Clarifications(db).InsertOneAsync(theirs);

        Assert.Empty((await GetJsonAsync(userId, "/me/clarifications"))
            .GetProperty("clarifications").EnumerateArray());

        // And the writes must 404 rather than mutate someone else's row.
        foreach (var action in new[] { "resolve", "defer", "drop" })
        {
            var response = await PostAsync(userId, $"/me/clarifications/{theirs["_id"].AsObjectId}/{action}", "{}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    // ---- 404s --------------------------------------------------------------

    [Theory]
    [InlineData("resolve")]
    [InlineData("defer")]
    [InlineData("drop")]
    public async Task answers_the_slices_own_not_found_for_a_malformed_id(string action)
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var response = await PostAsync(userId, $"/me/clarifications/not-an-id/{action}", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // NOT the kernel's generic `not_found` — these routes hand-throw their own
        // code and message, which the differ compares literally.
        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("clarification_not_found", error.GetProperty("code").GetString());
        Assert.Equal("That question is no longer here.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task answers_not_found_for_an_id_that_never_existed()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var response = await PostAsync(
            userId,
            $"/me/clarifications/{ObjectId.GenerateNewId()}/resolve",
            """{"answer":{"type":"option","index":0}}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Resolve: body validation -----------------------------------------

    [Theory]
    [InlineData("{}", "answer", "Required")]
    [InlineData("""{"answer":"x"}""", "answer", "Expected object, received string")]
    [InlineData("""{"answer":{"type":"nope"}}""", "answer", "Invalid discriminator value. Expected 'option' | 'custom'")]
    [InlineData("""{"answer":{"index":0}}""", "answer", "Invalid discriminator value. Expected 'option' | 'custom'")]
    [InlineData("""{"answer":{"type":"option"}}""", "answer", "Required")]
    [InlineData("""{"answer":{"type":"option","index":4}}""", "answer", "Number must be less than or equal to 3")]
    [InlineData("""{"answer":{"type":"option","index":-1}}""", "answer", "Number must be greater than or equal to 0")]
    [InlineData("""{"answer":{"type":"option","index":1.5}}""", "answer", "Expected integer, received float")]
    [InlineData("""{"answer":{"type":"custom"}}""", "answer", "Required")]
    [InlineData("""{"answer":{"type":"custom","text":""}}""", "answer", "String must contain at least 1 character(s)")]
    [InlineData("""{"answer":{"type":"custom","text":"   "}}""", "answer", "String must contain at least 1 character(s)")]
    [InlineData("""{"answer":{"type":"option","index":0},"timezone":""}""", "timezone", "String must contain at least 1 character(s)")]
    public async Task reports_a_bad_answer_through_the_flattened_details_shape(
        string body,
        string field,
        string message)
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var (userId, clarificationId, _) = await SeedResolvableAsync(db);

        var response = await PostAsync(userId, $"/me/clarifications/{clarificationId}/resolve", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("invalid_answer", error.GetProperty("code").GetString());
        Assert.Equal("Invalid answer payload.", error.GetProperty("message").GetString());

        // safeParse + error.flatten() → {formErrors, fieldErrors}, keyed on path[0].
        // KERNEL.md §2.3 — one of three shapes, and picking the wrong one is a silent
        // parity break no status check catches.
        var details = error.GetProperty("details");
        Assert.Empty(details.GetProperty("formErrors").EnumerateArray());
        Assert.Equal(message, details.GetProperty("fieldErrors").GetProperty(field)[0].GetString());
    }

    [Fact]
    public async Task rejects_a_custom_answer_over_five_hundred_characters()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var (userId, clarificationId, _) = await SeedResolvableAsync(db);

        var response = await PostAsync(
            userId,
            $"/me/clarifications/{clarificationId}/resolve",
            """{"answer":{"type":"custom","text":"@"}}""".Replace("@", new string('x', 501)));

        var details = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details");
        Assert.Equal(
            "String must contain at most 500 character(s)",
            details.GetProperty("fieldErrors").GetProperty("answer")[0].GetString());
    }

    [Fact]
    public async Task strips_unknown_body_keys_instead_of_rejecting_them()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var (userId, clarificationId, _) = await SeedResolvableAsync(db);

        // ResolveBodySchema is NOT .strict(). Only me.tasks bodies are.
        var response = await PostAsync(
            userId,
            $"/me/clarifications/{clarificationId}/resolve",
            """{"answer":{"type":"option","index":0},"bogus":true}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Resolve: the option branch ---------------------------------------

    [Fact]
    public async Task resolving_with_an_option_patches_the_task_and_closes_the_question()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var (userId, clarificationId, taskId) = await SeedResolvableAsync(db);

        var json = await PostJsonAsync(
            userId,
            $"/me/clarifications/{clarificationId}/resolve",
            """{"answer":{"type":"option","index":0}}""");

        var clarification = json.GetProperty("clarification");
        Assert.Equal("resolved", clarification.GetProperty("status").GetString());
        Assert.Equal("The 15th", clarification.GetProperty("answer").GetString());
        Assert.True(clarification.TryGetProperty("resolvedAt", out _));

        var task = json.GetProperty("task");
        Assert.Equal(taskId.ToString(), task.GetProperty("id").GetString());
        Assert.Equal("Renew the passport (15th)", task.GetProperty("title").GetString());
        Assert.Equal("2026-09-15T10:30:00.000Z", task.GetProperty("dueAt").GetString());

        // A patch carrying dueAt ALSO forces kind:'reminder' — a confirmed date is the
        // whole point of asking, so a task whose reminder was withheld on an uncertain
        // high-stakes guess starts firing now. The schedule is regenerated with it.
        Assert.Equal("reminder", task.GetProperty("kind").GetString());
        Assert.NotEmpty(task.GetProperty("reminders").EnumerateArray());
    }

    [Fact]
    public async Task an_option_with_no_date_leaves_the_task_passive()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var taskId = ObjectId.GenerateNewId();
        await Tasks(db).InsertOneAsync(Task(userId, taskId, "Passive item"));

        // Option carries only a label and the draft has no dueAt, so the patch is
        // EMPTY: nothing to write, and `kind` is not forced. Node reads the row back
        // rather than issuing an empty update, which the driver rejects outright.
        var row = Clar(userId, "Which one?", Epoch, taskId: taskId);
        row["options"] = new BsonArray { new BsonDocument { ["label"] = "Label only" } };
        // The draft's own dueAt is the FALLBACK when the option has none, so it has to
        // go too — otherwise the date lands anyway and the task becomes a reminder.
        row["draft"].AsBsonDocument.Remove("dueAt");
        await Clarifications(db).InsertOneAsync(row);

        var json = await PostJsonAsync(
            userId,
            $"/me/clarifications/{row["_id"].AsObjectId}/resolve",
            """{"answer":{"type":"option","index":0}}""");

        Assert.Equal("list", json.GetProperty("task").GetProperty("kind").GetString());
        Assert.Empty(json.GetProperty("task").GetProperty("reminders").EnumerateArray());
        Assert.Equal("Label only", json.GetProperty("clarification").GetProperty("answer").GetString());
    }

    [Fact]
    public async Task an_index_past_the_end_of_the_options_is_its_own_error()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var taskId = ObjectId.GenerateNewId();
        await Tasks(db).InsertOneAsync(Task(userId, taskId, "T"));

        var row = Clar(userId, "No options at all", Epoch, taskId: taskId);
        row["options"] = new BsonArray();
        await Clarifications(db).InsertOneAsync(row);

        var response = await PostAsync(
            userId,
            $"/me/clarifications/{row["_id"].AsObjectId}/resolve",
            """{"answer":{"type":"option","index":0}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // 0..3 passes the SCHEMA; being past the end of the stored array is a
        // different failure with a different code. 4+ is the schema's invalid_answer.
        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("invalid_option", error.GetProperty("code").GetString());
        Assert.Equal("That answer is no longer available.", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("details", out _));
    }

    // ---- Resolve: the custom branch ---------------------------------------

    [Fact]
    public async Task a_typed_answer_needs_ai_and_says_so_in_its_own_words()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var (userId, clarificationId, _) = await SeedResolvableAsync(db);

        var response = await PostAsync(
            userId,
            $"/me/clarifications/{clarificationId}/resolve",
            """{"answer":{"type":"custom","text":"the 20th actually"}}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // The SIXTH distinct ai_not_configured message in the API — the other five
        // belong to Matters. Copy-verbatim territory.
        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("ai_not_configured", error.GetProperty("code").GetString());
        Assert.Equal(
            "Typing your own answer needs AI configured. Pick one of the suggestions instead.",
            error.GetProperty("message").GetString());
    }

    // ---- Resolve: the short-circuits --------------------------------------

    [Theory]
    [InlineData("resolved")]
    [InlineData("dropped")]
    public async Task resolving_an_already_closed_question_is_idempotent(string status)
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var taskId = ObjectId.GenerateNewId();
        await Tasks(db).InsertOneAsync(Task(userId, taskId, "T"));

        var row = Clar(userId, "Already answered", Epoch, taskId: taskId, status: status);
        await Clarifications(db).InsertOneAsync(row);

        // A double-tap or a stale client must echo the current state, not create a
        // second task. The check runs BEFORE the body is parsed, so even a payload
        // that would otherwise be a 400 answers 200 here.
        var json = await PostJsonAsync(
            userId,
            $"/me/clarifications/{row["_id"].AsObjectId}/resolve",
            """{"answer":"total garbage"}""");

        Assert.Equal(status, json.GetProperty("clarification").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("task").ValueKind);
    }

    [Fact]
    public async Task a_question_whose_task_was_deleted_is_dropped_not_resurrected()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var taskId = ObjectId.GenerateNewId();

        var task = Task(userId, taskId, "Thrown away");
        task["deletedAt"] = Epoch.AddMinutes(30);
        await Tasks(db).InsertOneAsync(task);
        var row = Clar(userId, "Moot now", Epoch, taskId: taskId);
        await Clarifications(db).InsertOneAsync(row);

        var json = await PostJsonAsync(
            userId,
            $"/me/clarifications/{row["_id"].AsObjectId}/resolve",
            """{"answer":{"type":"option","index":0}}""");

        // The question is moot: close it out rather than resurrecting work the user
        // threw away.
        Assert.Equal("dropped", json.GetProperty("clarification").GetProperty("status").GetString());
        Assert.True(json.GetProperty("clarification").TryGetProperty("resolvedAt", out _));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("task").ValueKind);

        // No answer is recorded — nothing was answered.
        Assert.False(json.GetProperty("clarification").TryGetProperty("answer", out _));
    }

    [Fact]
    public async Task closes_out_a_legacy_row_that_has_no_task_id()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // Rows written before `taskId` existed have none. `save()` revalidates the
        // WHOLE document and taskId is required, so every terminal action on such a
        // row threw a ValidationError and 500'd — the question could never be cleared
        // and came back on every reload. The atomic $set close-out is what fixes it,
        // and this test is what stops a future refactor reintroducing a save().
        var row = Clar(userId, "Legacy", Epoch);
        row.Remove("taskId");
        await Clarifications(db).InsertOneAsync(row);

        var response = await PostAsync(
            userId,
            $"/me/clarifications/{row["_id"].AsObjectId}/resolve",
            """{"answer":{"type":"option","index":0}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Equal("dropped", json.GetProperty("clarification").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("task").ValueKind);

        var stored = await Clarifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", row["_id"]))
            .FirstAsync();
        Assert.Equal("dropped", stored["status"].AsString);

        // KNOWN DELTA, deliberately NOT asserted either way. Node omits `taskId` on
        // such a row; this port emits an all-zero ObjectId, because the document and
        // DTO types are non-nullable. Both live under Kernel/, so it is reported
        // rather than fixed here — see the note on ClarificationEndpoints. Asserting
        // the current output would pin the divergence; asserting Node's would fail a
        // green build for something this slice cannot change.
    }

    // ---- Defer -------------------------------------------------------------

    [Fact]
    public async Task defer_hides_the_question_for_seven_days_without_closing_it()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var row = Clar(userId, "Not now", Epoch);
        await Clarifications(db).InsertOneAsync(row);

        var before = DateTime.UtcNow;
        var json = await PostJsonAsync(userId, $"/me/clarifications/{row["_id"].AsObjectId}/defer", null);

        var clarification = json.GetProperty("clarification");

        // Status is NOT changed — the row stays `open` and simply drops out of
        // VisibleOpen() until the window passes.
        Assert.Equal("open", clarification.GetProperty("status").GetString());

        var deferredUntil = clarification.GetProperty("deferredUntil").GetDateTime();
        Assert.InRange(deferredUntil, before.AddDays(7).AddSeconds(-30), DateTime.UtcNow.AddDays(7).AddSeconds(30));

        // And it is gone from the list.
        Assert.Empty((await GetJsonAsync(userId, "/me/clarifications"))
            .GetProperty("clarifications").EnumerateArray());
    }

    [Fact]
    public async Task the_echoed_updated_at_is_the_pre_update_value_while_the_row_moves_on()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var row = Clar(userId, "Echo check", Epoch);
        await Clarifications(db).InsertOneAsync(row);

        var json = await PostJsonAsync(userId, $"/me/clarifications/{row["_id"].AsObjectId}/defer", null);

        // TWO behaviours that pull in opposite directions, and a port can easily get
        // one right and the other wrong.
        //
        // `closeOut` does an atomic updateOne — into which Mongoose injects a fresh
        // `updatedAt`, and the .NET driver does NOT, so the repository adds it by
        // hand. It then does `doc.set(patch)`, which touches only the PATCH fields in
        // memory. The route echoes that in-memory document, so the response carries
        // the PRE-update `updatedAt` beside a current `deferredUntil`.
        Assert.Equal(
            Epoch.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            json.GetProperty("clarification").GetProperty("updatedAt").GetString());

        var stored = await Clarifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", row["_id"]))
            .FirstAsync();

        // ...while the STORED row's updatedAt has advanced.
        Assert.True(
            stored["updatedAt"].ToUniversalTime() > Epoch,
            "the stored updatedAt must advance even though the echo does not");

        // The patched field itself is identical in both places.
        Assert.Equal(
            json.GetProperty("clarification").GetProperty("deferredUntil").GetDateTime(),
            stored["deferredUntil"].ToUniversalTime());
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("dropped")]
    public async Task defer_on_a_closed_question_is_a_pure_read(string status)
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var row = Clar(userId, "Already closed", Epoch, status: status);
        await Clarifications(db).InsertOneAsync(row);

        var json = await PostJsonAsync(userId, $"/me/clarifications/{row["_id"].AsObjectId}/defer", null);

        Assert.Equal(status, json.GetProperty("clarification").GetProperty("status").GetString());
        Assert.False(json.GetProperty("clarification").TryGetProperty("deferredUntil", out _));

        var stored = await Clarifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", row["_id"]))
            .FirstAsync();
        Assert.False(stored.Contains("deferredUntil"));
        Assert.Equal(Epoch, stored["updatedAt"].ToUniversalTime());
    }

    // ---- Drop --------------------------------------------------------------

    [Fact]
    public async Task drop_discards_the_question_without_creating_anything()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var row = Clar(userId, "Never mind", Epoch);
        await Clarifications(db).InsertOneAsync(row);

        var json = await PostJsonAsync(userId, $"/me/clarifications/{row["_id"].AsObjectId}/drop", null);

        Assert.Equal("dropped", json.GetProperty("clarification").GetProperty("status").GetString());
        Assert.True(json.GetProperty("clarification").TryGetProperty("resolvedAt", out _));

        Assert.Empty((await GetJsonAsync(userId, "/me/clarifications"))
            .GetProperty("clarifications").EnumerateArray());
    }

    [Fact]
    public async Task drop_on_an_already_dropped_question_is_a_pure_read()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);
        var row = Clar(userId, "Gone already", Epoch, status: "dropped");
        row["resolvedAt"] = Epoch.AddMinutes(1);
        await Clarifications(db).InsertOneAsync(row);

        var json = await PostJsonAsync(userId, $"/me/clarifications/{row["_id"].AsObjectId}/drop", null);

        // The original resolvedAt is NOT overwritten.
        Assert.Equal(
            Epoch.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            json.GetProperty("clarification").GetProperty("resolvedAt").GetString());
    }

    // ---- The cross-surface invariant ---------------------------------------

    [Fact]
    public async Task the_list_and_the_needs_input_count_agree()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // The reason VisibleOpen() exists at all: the dashboard once derived its
        // number from a 50-capped page while the digest aggregated the whole
        // collection, so the two disagreed for anyone with a real backlog. Both
        // surfaces must compose the SAME predicate — including the deferral window.
        await Clarifications(db).InsertManyAsync(new[]
        {
            Clar(userId, "visible a", Epoch),
            Clar(userId, "visible b", Epoch.AddMinutes(1)),
            Clar(userId, "deferred away", Epoch.AddMinutes(2), deferredUntil: Epoch.AddYears(50)),
            Clar(userId, "closed", Epoch.AddMinutes(3), status: "resolved"),
        });

        var listed = (await GetJsonAsync(userId, "/me/clarifications"))
            .GetProperty("clarifications").GetArrayLength();
        var counted = (await GetJsonAsync(userId, "/me/tasks/counts"))
            .GetProperty("counts").GetProperty("needsInput").GetInt32();

        Assert.Equal(2, listed);
        Assert.Equal(listed, counted);
    }

    // ---- Helpers -----------------------------------------------------------

    private HttpClient AuthedClient(ObjectId userId)
    {
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(userId.ToString(), "h-clar@probe.com"));
        return client;
    }

    private async Task<JsonElement> GetJsonAsync(ObjectId userId, string path)
    {
        var response = await AuthedClient(userId).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private Task<HttpResponseMessage> PostAsync(ObjectId userId, string path, string? body) =>
        AuthedClient(userId).PostAsync(path, body is null ? null : JsonBody(body));

    private async Task<JsonElement> PostJsonAsync(ObjectId userId, string path, string? body)
    {
        var response = await PostAsync(userId, path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>A fresh owner plus an empty slate, so tests cannot see each other.</summary>
    private static async Task<ObjectId> ResetAsync(IMongoDatabase db)
    {
        var userId = ObjectId.GenerateNewId();
        await Clarifications(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));
        await Tasks(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));
        return userId;
    }

    /// <summary>The common fixture: a live task and an open, two-option question about it.</summary>
    private static async Task<(ObjectId UserId, ObjectId ClarificationId, ObjectId TaskId)> SeedResolvableAsync(
        IMongoDatabase db)
    {
        var userId = await ResetAsync(db);
        var taskId = ObjectId.GenerateNewId();

        await Tasks(db).InsertOneAsync(Task(userId, taskId, "Renew the passport"));

        var row = Clar(userId, "Is it the 15th or the 18th?", Epoch, taskId: taskId);
        await Clarifications(db).InsertOneAsync(row);

        return (userId, row["_id"].AsObjectId, taskId);
    }

    private static BsonDocument Clar(
        ObjectId userId,
        string question,
        DateTime createdAt,
        ObjectId? taskId = null,
        string status = "open",
        DateTime? deferredUntil = null)
    {
        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["taskId"] = taskId ?? ObjectId.GenerateNewId(),
            ["status"] = status,
            ["draft"] = new BsonDocument
            {
                ["title"] = "Renew the passport",
                ["domain"] = "home",
                ["priority"] = "normal",
                ["tags"] = new BsonArray { "admin" },
                ["dueAt"] = new DateTime(2026, 9, 18, 8, 0, 0, DateTimeKind.Utc),
            },
            ["question"] = question,
            ["kind"] = "date",
            ["costOfWrong"] = "high",
            ["options"] = new BsonArray
            {
                new BsonDocument
                {
                    ["label"] = "The 15th",
                    ["dueAt"] = new DateTime(2026, 9, 15, 10, 30, 0, DateTimeKind.Utc),
                    ["title"] = "Renew the passport (15th)",
                },
                new BsonDocument { ["label"] = "The 18th", ["notes"] = "Second slot" },
            },
            ["createdAt"] = createdAt,
            ["updatedAt"] = createdAt,
            ["__v"] = 0,
        };

        if (deferredUntil.HasValue)
        {
            doc["deferredUntil"] = deferredUntil.Value;
        }

        return doc;
    }

    private static BsonDocument Task(ObjectId userId, ObjectId taskId, string title) => new()
    {
        ["_id"] = taskId,
        ["userId"] = userId,
        ["title"] = title,
        ["domain"] = "home",
        ["kind"] = "list",
        ["status"] = "open",
        ["priority"] = "normal",
        ["subtasks"] = new BsonArray(),
        ["tags"] = new BsonArray { "admin" },
        ["reminders"] = new BsonArray(),
        ["rescheduleCount"] = 0,
        ["createdAt"] = Epoch,
        ["updatedAt"] = Epoch,
        ["__v"] = 0,
    };

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
