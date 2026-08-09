using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Tasks;

/// <summary>
/// Its own database, so a parallel slice's test run cannot see these rows.
/// </summary>
public sealed class TasksWebApplicationFactory : KernelWebApplicationFactory
{
    public const string TasksDatabase = "kitto_parity_dotnet_c_tests";

    public TasksWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", TasksDatabase);
    }
}

/// <summary>
/// The Matters slice, against the live Node behaviour captured on the reference
/// server at <c>:4200</c>.
///
/// <para>
/// Split deliberately into two kinds of case. <b>Validation, auth and AI-gate
/// cases touch no database</b> — every one of those checks runs before the first
/// Mongo call, so they always execute and are the suite's real floor. <b>Data
/// cases</b> skip (rather than fail) when the parity Mongo instance is not
/// running, following <c>UsageQuotaTests.TryCreateStore</c>.
/// </para>
/// </summary>
public sealed class TaskEndpointTests : IClassFixture<TasksWebApplicationFactory>
{
    private readonly TasksWebApplicationFactory _factory;

    public TaskEndpointTests(TasksWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Strict query binding ---------------------------------------------
    // This route family is the ONLY one in the API with strict query binding, so
    // these are the cases that catch a regression to the lenient default.

    [Fact]
    public async Task rejects_an_unknown_query_parameter_with_the_flattened_details_shape()
    {
        // Act
        var json = await GetJsonAsync("/me/tasks?bogus=1", HttpStatusCode.BadRequest);

        // Assert
        var error = json.GetProperty("error");
        Assert.Equal("invalid_query", error.GetProperty("code").GetString());
        Assert.Equal("Invalid task list query.", error.GetProperty("message").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(
            "Unrecognized key(s) in object: 'bogus'",
            details.GetProperty("formErrors")[0].GetString());
        Assert.Empty(details.GetProperty("fieldErrors").EnumerateObject());
    }

    [Fact]
    public async Task rejects_an_empty_csv_enum_parameter()
    {
        var json = await GetJsonAsync("/me/tasks?status=", HttpStatusCode.BadRequest);

        var fieldErrors = json.GetProperty("error").GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal("must not be empty", fieldErrors.GetProperty("status")[0].GetString());
    }

    [Theory]
    [InlineData("dueBefore")]
    [InlineData("createdAfter")]
    [InlineData("completedBefore")]
    public async Task rejects_a_bare_date_where_zod_demands_a_full_instant(string field)
    {
        // zod's .datetime() requires an offset; DateTime.TryParse would accept this,
        // which is why the slice does its own parse.
        var json = await GetJsonAsync($"/me/tasks?{field}=2026-09-01", HttpStatusCode.BadRequest);

        var fieldErrors = json.GetProperty("error").GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal("Invalid datetime", fieldErrors.GetProperty(field)[0].GetString());
    }

    [Fact]
    public async Task rejects_an_unknown_parameter_on_counts_with_its_own_message()
    {
        var json = await GetJsonAsync("/me/tasks/counts?bogus=1", HttpStatusCode.BadRequest);

        var error = json.GetProperty("error");
        Assert.Equal("invalid_query", error.GetProperty("code").GetString());
        Assert.Equal("Invalid counts query.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task accumulates_every_query_issue_into_one_response()
    {
        // The reader accumulates and throws once, so a request with three problems
        // reports three, not just the first.
        var json = await GetJsonAsync(
            "/me/tasks?status=&priority=&dueBefore=2026-09-01",
            HttpStatusCode.BadRequest);

        var fieldErrors = json.GetProperty("error").GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal(3, fieldErrors.EnumerateObject().Count());
    }

    // ---- Strict body binding ----------------------------------------------

    [Fact]
    public async Task rejects_an_unknown_key_in_a_create_body()
    {
        var json = await PostJsonAsync(
            "/me/tasks",
            """{"title":"x","domain":"home","bogus":1}""",
            HttpStatusCode.BadRequest);

        var error = json.GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal("Invalid task payload.", error.GetProperty("message").GetString());
        Assert.Equal(
            "Unrecognized key(s) in object: 'bogus'",
            error.GetProperty("details").GetProperty("formErrors")[0].GetString());
    }

    [Fact]
    public async Task accepts_an_unknown_key_in_a_bulk_body_because_that_schema_is_lenient()
    {
        // The one documented divergence inside this route family: BulkTargetSchema
        // and BulkActionSchema carry no .strict(), so an unknown key is stripped.
        // A 400 here would mean the body was rejected; anything else means it was
        // accepted and the request went on to do real work.
        //
        // Note preview is z.intersection(target, action), so it needs an ACTION as
        // well as a target — verified live: omitting it is a 400 on the discriminator,
        // which would mask the leniency this case is about.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks/bulk/preview",
            """{"ids":["64b000000000000000000001"],"action":"complete","bogus":1}""");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task reports_the_target_xor_as_a_form_level_issue()
    {
        var json = await PostJsonAsync(
            "/me/tasks/bulk/preview",
            """{"action":"complete"}""",
            HttpStatusCode.BadRequest);

        var details = json.GetProperty("error").GetProperty("details");
        Assert.Equal("provide exactly one of ids or filter", details.GetProperty("formErrors")[0].GetString());
    }

    [Fact]
    public async Task supplying_both_a_target_id_list_and_a_filter_is_the_same_xor_failure()
    {
        var json = await PostJsonAsync(
            "/me/tasks/bulk/preview",
            """{"ids":["64b000000000000000000001"],"filter":{"status":"open"},"action":"complete"}""",
            HttpStatusCode.BadRequest);

        var details = json.GetProperty("error").GetProperty("details");
        Assert.Equal("provide exactly one of ids or filter", details.GetProperty("formErrors")[0].GetString());
    }

    [Fact]
    public async Task reports_a_bad_bulk_action_with_the_discriminator_message()
    {
        var json = await PostJsonAsync(
            "/me/tasks/bulk",
            """{"ids":["64b000000000000000000001"],"action":"nope"}""",
            HttpStatusCode.BadRequest);

        var fieldErrors = json.GetProperty("error").GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal(
            "Invalid discriminator value. Expected 'delete' | 'complete' | 'snooze' | 'setDomain' | 'addTags'",
            fieldErrors.GetProperty("action")[0].GetString());
    }

    [Fact]
    public async Task treats_an_empty_subtask_patch_as_a_form_level_issue()
    {
        var json = await PatchJsonAsync(
            "/me/tasks/64b000000000000000000001/subtasks/64c000000000000000000001",
            "{}",
            HttpStatusCode.BadRequest);

        var details = json.GetProperty("error").GetProperty("details");
        Assert.Equal("must include text or done", details.GetProperty("formErrors")[0].GetString());
    }

    // ---- The five 503 messages --------------------------------------------
    // Copy-verbatim territory: the differ compares these literally, and four of the
    // five are near-identical sentences with different subjects.

    [Theory]
    [InlineData("/me/tasks/search", """{"query":"passport"}""", "Search by description is unavailable.")]
    [InlineData("/me/tasks/summarize", """{"from":"2026-08-01T00:00:00.000Z","to":"2026-08-31T00:00:00.000Z"}""",
        "Summaries are unavailable right now.")]
    [InlineData("/me/tasks/estimate-backlog", "{}", "Estimates are unavailable right now.")]
    [InlineData("/me/tasks/categorize", """{"ids":["64b000000000000000000001"]}""",
        "Categorising is unavailable right now.")]
    [InlineData("/me/tasks/translate", """{"locale":"ar"}""", "Translating is unavailable right now.")]
    public async Task answers_503_with_the_route_s_own_literal_message(string path, string body, string message)
    {
        var json = await PostJsonAsync(path, body, HttpStatusCode.ServiceUnavailable);

        var error = json.GetProperty("error");
        Assert.Equal("ai_not_configured", error.GetProperty("code").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("details", out _));
    }

    // ---- The inconsistent gate order --------------------------------------

    [Fact]
    public async Task summarize_checks_ai_before_the_body_so_a_malformed_range_is_503()
    {
        // THE ODD ONE OUT. A port that tidied this into the majority order would
        // turn this 503 into a 400 and break the client's error handling on exactly
        // one route.
        var json = await PostJsonAsync("/me/tasks/summarize", """{"from":"bad"}""",
            HttpStatusCode.ServiceUnavailable);

        Assert.Equal("ai_not_configured", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/me/tasks/search", """{"query":""}""", "Invalid search request.")]
    [InlineData("/me/tasks/estimate-backlog", """{"limit":99}""", "Invalid backfill request.")]
    [InlineData("/me/tasks/categorize", "{}", "Invalid categorize request.")]
    [InlineData("/me/tasks/translate", """{"locale":"fr"}""", "Invalid translate request.")]
    public async Task the_other_four_validate_before_the_ai_gate_so_a_bad_body_is_400(
        string path, string body, string message)
    {
        var json = await PostJsonAsync(path, body, HttpStatusCode.BadRequest);

        var error = json.GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
    }

    // ---- Not-found literals ------------------------------------------------

    [Theory]
    [InlineData("/me/tasks/not-an-id")]
    [InlineData("/me/tasks/64b0000000000000000000ff")]
    public async Task a_malformed_or_missing_task_id_is_the_route_s_own_404(string path)
    {
        // Deliberately NOT the kernel's CastError 404 ("not_found" / "Not found") —
        // the route checks the id itself and throws its own code first.
        var json = await GetJsonAsync(path, HttpStatusCode.NotFound);

        var error = json.GetProperty("error");
        Assert.Equal("task_not_found", error.GetProperty("code").GetString());
        Assert.Equal("Task no longer exists.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task a_malformed_undo_token_gets_the_short_message()
    {
        var json = await PostJsonAsync("/me/tasks/undo/zzz", null, HttpStatusCode.NotFound);

        var error = json.GetProperty("error");
        Assert.Equal("undo_not_found", error.GetProperty("code").GetString());
        Assert.Equal("That change is no longer undoable.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task a_well_formed_but_unknown_undo_token_names_the_thirty_day_window()
    {
        // Same code, different sentence. The short message is a strict prefix of
        // this one up to the period, so a prefix match cannot tell them apart.
        var json = await PostJsonAsync("/me/tasks/undo/64d000000000000000000001", null, HttpStatusCode.NotFound);

        var error = json.GetProperty("error");
        Assert.Equal("undo_not_found", error.GetProperty("code").GetString());
        Assert.Equal(
            "That change is no longer undoable — undo is available for 30 days.",
            error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/me/tasks/categorize/nope/discard")]
    [InlineData("/me/tasks/categorize/64d000000000000000000001/discard")]
    public async Task an_unknown_proposal_is_the_same_404_whether_the_id_parses_or_not(string path)
    {
        var json = await PostJsonAsync(path, null, HttpStatusCode.NotFound);

        var error = json.GetProperty("error");
        Assert.Equal("proposal_not_found", error.GetProperty("code").GetString());
        Assert.Equal("That suggestion has expired.", error.GetProperty("message").GetString());
    }

    // ---- Auth --------------------------------------------------------------

    [Theory]
    [InlineData("/me/tasks")]
    [InlineData("/me/tasks/counts")]
    [InlineData("/me/tasks/tags")]
    [InlineData("/me/tasks/trash")]
    [InlineData("/me/tasks/categorize/pending")]
    [InlineData("/me/tasks/translate/quota")]
    public async Task every_read_route_requires_a_token(string path)
    {
        var response = await _factory.CreateApiClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- Explicit nulls the client branches on -----------------------------
    // The kernel pins DefaultIgnoreCondition = WhenWritingNull globally, which is
    // right for Mongoose documents and wrong for these three envelope fields.

    [Fact]
    public async Task the_list_envelope_carries_nextCursor_as_an_explicit_null()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await SeedTaskAsync(tasks, userId, "Only one");

        var json = await GetJsonAsync("/me/tasks", HttpStatusCode.OK, userId);

        Assert.True(json.TryGetProperty("nextCursor", out var cursor));
        Assert.Equal(JsonValueKind.Null, cursor.ValueKind);
    }

    [Fact]
    public async Task the_pending_proposal_envelope_carries_proposal_as_an_explicit_null()
    {
        var json = await GetJsonAsync("/me/tasks/categorize/pending", HttpStatusCode.OK);

        Assert.True(json.TryGetProperty("proposal", out var proposal));
        Assert.Equal(JsonValueKind.Null, proposal.ValueKind);
    }

    [Fact]
    public async Task a_bulk_run_that_changes_nothing_reports_a_null_undo_token()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();

        // A filter that matches no rows: nothing is journaled, so there is no op to
        // undo and the token is null rather than absent.
        var json = await PostJsonAsync(
            "/me/tasks/bulk",
            """{"filter":{"domain":"family"},"action":"delete"}""",
            HttpStatusCode.OK,
            userId);

        Assert.Equal(0, json.GetProperty("affected").GetInt32());
        Assert.True(json.TryGetProperty("undoToken", out var token));
        Assert.Equal(JsonValueKind.Null, token.ValueKind);
    }

    // ---- The i18n overlay --------------------------------------------------

    [Fact]
    public async Task the_two_overlay_endpoints_strip_i18n_and_every_other_one_ships_it()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var taskId = await SeedTranslatedTaskAsync(tasks, userId);

        // GET /me/tasks and GET /me/tasks/{id} — stripped.
        var list = await GetJsonAsync("/me/tasks", HttpStatusCode.OK, userId);
        Assert.False(list.GetProperty("tasks")[0].TryGetProperty("i18n", out _));

        var one = await GetJsonAsync($"/me/tasks/{taskId}", HttpStatusCode.OK, userId);
        Assert.False(one.GetProperty("task").TryGetProperty("i18n", out _));

        // Trash ships the raw toJSON, i18n included. That field leaks by design.
        await tasks.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", taskId),
            Builders<BsonDocument>.Update.Set("deletedAt", DateTime.UtcNow));

        var trash = await GetJsonAsync("/me/tasks/trash", HttpStatusCode.OK, userId);
        Assert.True(trash.GetProperty("tasks")[0].TryGetProperty("i18n", out _));
    }

    [Fact]
    public async Task subtask_text_is_never_translated_because_the_lookup_key_is_the_string_undefined()
    {
        // FROZEN BUG, ported deliberately. Node keys the overlay on `sub._id`, but
        // toJSON already renamed it to `id`, so the key is the literal "undefined"
        // and the lookup misses on every row. Fixing it would be a behavioural
        // change the contract does not permit.
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();

        // The overlay keys off the READER's locale, so the user row is what makes
        // this case exercise anything at all.
        await SeedUserLocaleAsync(userId, "ar");
        var taskId = await SeedTranslatedTaskAsync(tasks, userId);

        var json = await GetJsonAsync($"/me/tasks/{taskId}", HttpStatusCode.OK, userId);
        var task = json.GetProperty("task");

        // The title IS overlaid...
        Assert.Equal("مترجم", task.GetProperty("title").GetString());

        // ...and the subtask text is NOT, despite a translation being present for it.
        Assert.Equal("canonical step", task.GetProperty("subtasks")[0].GetProperty("text").GetString());
    }

    // ---- PATCH null-versus-omitted -----------------------------------------

    [Fact]
    public async Task an_explicit_null_clears_a_field_and_an_omitted_key_leaves_it_alone()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var taskId = await SeedTaskAsync(tasks, userId, "Has both", task =>
        {
            task["dueAt"] = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
            task["notes"] = "keep me";
        });

        // Explicit null clears dueAt; notes is not mentioned and survives.
        var json = await PatchJsonAsync($"/me/tasks/{taskId}", """{"dueAt":null}""", HttpStatusCode.OK, userId);
        var task = json.GetProperty("task");

        Assert.False(task.TryGetProperty("dueAt", out _));
        Assert.Equal("keep me", task.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task rescheduleCount_increments_only_when_the_due_date_moves_strictly_later()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var original = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);

        async Task<int> PatchDueAsync(DateTime due)
        {
            var taskId = await SeedTaskAsync(tasks, userId, "Slipping", t => t["dueAt"] = original);
            var body = $$"""{"dueAt":"{{due:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}"}""";
            var json = await PatchJsonAsync($"/me/tasks/{taskId}", body, HttpStatusCode.OK, userId);
            return json.GetProperty("task").GetProperty("rescheduleCount").GetInt32();
        }

        Assert.Equal(1, await PatchDueAsync(original.AddDays(1)));
        Assert.Equal(0, await PatchDueAsync(original.AddDays(-1)));
        Assert.Equal(0, await PatchDueAsync(original));
    }

    // ---- The two reachable 500s -------------------------------------------

    [Fact]
    public async Task creating_a_reminder_with_no_due_date_is_a_500_and_that_is_the_contract()
    {
        // Passes zod, fails the Mongoose pre('validate') invariant, and the error
        // handler does not map ValidationError. Frozen.
        var json = await PostJsonAsync(
            "/me/tasks",
            """{"title":"x","domain":"home","kind":"reminder"}""",
            HttpStatusCode.InternalServerError);

        var error = json.GetProperty("error");
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.Equal("Internal server error", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task clearing_a_reminder_s_due_date_succeeds_and_then_breaks_all_three_subtask_routes()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        // The subtask must EXIST: subtask_not_found is a 404 raised before save(),
        // so a random id would short-circuit past the revalidation. Verified live.
        var subtaskId = ObjectId.GenerateNewId();
        var taskId = await SeedTaskAsync(tasks, userId, "Reminder", task =>
        {
            task["kind"] = "reminder";
            task["dueAt"] = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
            task["subtasks"] = new BsonArray
            {
                new BsonDocument
                {
                    ["_id"] = subtaskId,
                    ["text"] = "step",
                    ["done"] = false,
                    ["createdAt"] = now,
                },
            };
        });

        // The update path uses findOneAndUpdate, which skips validators — so this
        // SUCCEEDS and leaves an invalid document behind.
        await PatchJsonAsync($"/me/tasks/{taskId}", """{"dueAt":null}""", HttpStatusCode.OK, userId);

        // All three subtask routes go through save(), which revalidates the whole
        // document, so all three 500 from here on.
        var add = await SendAsync(HttpMethod.Post, $"/me/tasks/{taskId}/subtasks", """{"text":"a"}""", userId);
        Assert.Equal(HttpStatusCode.InternalServerError, add.StatusCode);

        var patch = await SendAsync(HttpMethod.Patch, $"/me/tasks/{taskId}/subtasks/{subtaskId}",
            """{"done":true}""", userId);
        Assert.Equal(HttpStatusCode.InternalServerError, patch.StatusCode);

        var delete = await SendAsync(HttpMethod.Delete, $"/me/tasks/{taskId}/subtasks/{subtaskId}", null, userId);
        Assert.Equal(HttpStatusCode.InternalServerError, delete.StatusCode);
    }

    // ---- Subtask routes return the parent ----------------------------------

    [Fact]
    public async Task adding_a_subtask_returns_the_whole_parent_task()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var taskId = await SeedTaskAsync(tasks, userId, "Parent");

        var response = await SendAsync(HttpMethod.Post, $"/me/tasks/{taskId}/subtasks",
            """{"text":"  Buy folder  "}""", userId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await ReadJsonAsync(response);
        var task = json.GetProperty("task");

        Assert.Equal(taskId.ToString(), task.GetProperty("id").GetString());
        Assert.Equal("Parent", task.GetProperty("title").GetString());
        Assert.Equal("Buy folder", task.GetProperty("subtasks")[0].GetProperty("text").GetString());
    }

    // ---- Tag normalisation --------------------------------------------------

    [Fact]
    public async Task tags_are_normalised_deduped_and_silently_capped_at_ten()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var body = new
        {
            title = "x",
            domain = "home",
            tags = new[]
            {
                "  Big Tag ", "BIG TAG", "a!!!", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9",
            },
        };

        var response = await SendAsync(HttpMethod.Post, "/me/tasks", JsonSerializer.Serialize(body));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await ReadJsonAsync(response);
        var tags = json.GetProperty("task").GetProperty("tags").EnumerateArray()
            .Select(t => t.GetString()).ToArray();

        // "Big Tag" and "BIG TAG" collapse to one; the list is capped at 10 with no
        // error — the ARRAY limit (20) is the one that errors.
        Assert.Equal(10, tags.Length);
        Assert.Equal("big-tag", tags[0]);
        Assert.Equal("a", tags[1]);
    }

    // ---- Helpers -----------------------------------------------------------

    private async Task<JsonElement> GetJsonAsync(string path, HttpStatusCode expected, ObjectId? userId = null)
    {
        var response = await SendAsync(HttpMethod.Get, path, null, userId);
        Assert.Equal(expected, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<JsonElement> PostJsonAsync(
        string path, string? body, HttpStatusCode expected, ObjectId? userId = null)
    {
        var response = await SendAsync(HttpMethod.Post, path, body, userId);
        Assert.Equal(expected, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<JsonElement> PatchJsonAsync(
        string path, string? body, HttpStatusCode expected, ObjectId? userId = null)
    {
        var response = await SendAsync(HttpMethod.Patch, path, body, userId);
        Assert.Equal(expected, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? body = null, ObjectId? userId = null)
    {
        var id = userId ?? ObjectId.GenerateNewId();
        var request = new HttpRequestMessage(method, path);
        var token = KernelPipelineTests.NodeShapedToken(id.ToString(), $"{id}@example.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _factory.CreateApiClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>
    /// Seeded as a raw <c>BsonDocument</c> so unset optionals are genuinely absent
    /// rather than stored as null — the distinction the DTO layer exists to keep.
    /// </summary>
    private static async Task<ObjectId> SeedTaskAsync(
        IMongoCollection<BsonDocument> tasks,
        ObjectId userId,
        string title,
        Action<BsonDocument>? customise = null)
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
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

        customise?.Invoke(task);
        await tasks.InsertOneAsync(task);
        return task["_id"].AsObjectId;
    }

    /// <summary>A task with an Arabic overlay, including a subtask translation.</summary>
    private static async Task<ObjectId> SeedTranslatedTaskAsync(
        IMongoCollection<BsonDocument> tasks,
        ObjectId userId)
    {
        var subtaskId = ObjectId.GenerateNewId();
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        return await SeedTaskAsync(tasks, userId, "canonical title", task =>
        {
            task["sourceLocale"] = "en";
            task["subtasks"] = new BsonArray
            {
                new BsonDocument
                {
                    ["_id"] = subtaskId,
                    ["text"] = "canonical step",
                    ["done"] = false,
                    ["createdAt"] = now,
                },
            };
            task["i18n"] = new BsonDocument
            {
                ["ar"] = new BsonDocument
                {
                    ["title"] = "مترجم",
                    ["notes"] = "ملاحظات",

                    // Keyed on the REAL subtask id. Node would need the literal
                    // string "undefined" here to ever hit, which is the frozen bug.
                    ["subtasks"] = new BsonDocument { [subtaskId.ToString()] = "خطوة" },
                    ["at"] = now,
                },
            };
        });
    }

    /// <summary>The reader's locale is what selects an <c>i18n</c> overlay.</summary>
    private static async Task SeedUserLocaleAsync(ObjectId userId, string locale)
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        await database.GetCollection<BsonDocument>(MongoCollections.Users).InsertOneAsync(new BsonDocument
        {
            ["_id"] = userId,
            ["email"] = $"{userId}@example.test",
            ["passwordHash"] = "x",
            ["timezone"] = "Africa/Cairo",
            ["locale"] = locale,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        });
    }

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
                .GetDatabase(TasksWebApplicationFactory.TasksDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
