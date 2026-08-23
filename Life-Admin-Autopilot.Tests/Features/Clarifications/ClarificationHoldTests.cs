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

    // ---- Several gaps, one matter ------------------------------------------
    //
    // The named failure, 2026-08-16: "remind me today to go to the friend" was held
    // ONCE, as "What time should I remind you — and which friend are you visiting?",
    // carrying time chips. The user tapped "9 am"; the row resolved, the task became
    // a 9am reminder, and the which-friend gap silently ceased to exist. A tapped
    // option can only ever answer ONE question, so N gaps need N rows.

    [Fact]
    public async Task two_gaps_become_two_questions_against_the_one_task()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Go visit my friend",
          "domain": "family",
          "question": "What time should I remind you?",
          "kind": "date",
          "sourceText": "remind me today to go to the friend",
          "timezone": "Africa/Cairo",
          "questions": [
            {
              "question": "What time should I remind you?",
              "kind": "date",
              "options": [
                {"label": "9 am", "dueAt": "2026-08-17T09:00:00+03:00"},
                {"label": "6 pm", "dueAt": "2026-08-17T18:00:00+03:00"}
              ]
            },
            {
              "question": "Which friend are you visiting?",
              "kind": "detail",
              "options": []
            }
          ]
        }
        """);

        // ONE task. holdForClarification has no task_id, so a second hold call would
        // have filed a duplicate — which is exactly why the two gaps got folded into
        // one sentence in the first place.
        var task = json.GetProperty("task");
        Assert.Equal("Go visit my friend", task.GetProperty("title").GetString());
        Assert.Equal(
            1,
            await Tasks(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));

        // TWO rows, both pointing at it.
        var rows = json.GetProperty("clarifications");
        Assert.Equal(2, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Assert.Equal(task.GetProperty("id").GetString(), row.GetProperty("taskId").GetString());
            Assert.Equal("open", row.GetProperty("status").GetString());

            // Every row describes the same matter and quotes the same words.
            Assert.Equal("Go visit my friend", row.GetProperty("draft").GetProperty("title").GetString());
            Assert.Equal(
                "remind me today to go to the friend",
                row.GetProperty("sourceText").GetString());
        }

        // Each gap keeps its OWN answer slot: the date question has the time chips,
        // the detail question has none because the user types that one.
        Assert.Equal("date", rows[0].GetProperty("kind").GetString());
        Assert.Equal(2, rows[0].GetProperty("options").GetArrayLength());
        Assert.Equal("Which friend are you visiting?", rows[1].GetProperty("question").GetString());
        Assert.Equal("detail", rows[1].GetProperty("kind").GetString());
        Assert.Empty(rows[1].GetProperty("options").EnumerateArray());

        // `clarification` stays the FIRST row, so a caller written against the
        // single-question response is unaffected.
        Assert.Equal(
            rows[0].GetProperty("id").GetString(),
            json.GetProperty("clarification").GetProperty("id").GetString());

        // Both are on the surface the home banner and the card stack read.
        var listed = (await GetJsonAsync(userId, "/me/clarifications")).GetProperty("clarifications");
        Assert.Equal(2, listed.GetArrayLength());
    }

    [Fact]
    public async Task answering_the_date_promotes_the_task_and_leaves_the_sibling_open()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Go visit my friend",
          "domain": "family",
          "question": "What time should I remind you?",
          "kind": "date",
          "timezone": "Africa/Cairo",
          "questions": [
            {
              "question": "What time should I remind you?",
              "kind": "date",
              "options": [{"label": "9 am", "dueAt": "2026-08-17T09:00:00+03:00"}]
            },
            {"question": "Which friend are you visiting?", "kind": "detail", "options": []}
          ]
        }
        """);

        var rows = json.GetProperty("clarifications");
        var dateId = rows[0].GetProperty("id").GetString();
        var siblingId = rows[1].GetProperty("id").GetString();

        // Withheld until the date is confirmed: costOfWrong defaults to 'high'.
        Assert.Equal("list", json.GetProperty("task").GetProperty("kind").GetString());

        var resolved = await ResolveAsync(userId, dateId!, index: 0);

        // Promotion is per the DATE answer, exactly as it was for a single question.
        // An open non-date sibling does not hold the reminder back — the guard exists
        // to stop a GUESSED date firing, and this date is no longer a guess.
        var patched = resolved.GetProperty("task");
        Assert.Equal("reminder", patched.GetProperty("kind").GetString());
        Assert.Equal("2026-08-17T06:00:00.000Z", patched.GetProperty("dueAt").GetString());
        Assert.NotEmpty(patched.GetProperty("reminders").EnumerateArray());
        Assert.Equal("resolved", resolved.GetProperty("clarification").GetProperty("status").GetString());

        // And the sibling is untouched. Nothing cascades between rows: they are
        // independent documents, and only a DELETED task closes them all out.
        var listed = (await GetJsonAsync(userId, "/me/clarifications")).GetProperty("clarifications");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal(siblingId, listed[0].GetProperty("id").GetString());
        Assert.Equal("open", listed[0].GetProperty("status").GetString());
        Assert.Equal("Which friend are you visiting?", listed[0].GetProperty("question").GetString());
    }

    [Fact]
    public async Task an_entry_inherits_what_it_omits_but_an_explicit_empty_is_not_omitted()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Renew the passport",
          "domain": "home",
          "question": "Is it the 15th or the 18th?",
          "kind": "date",
          "costOfWrong": "high",
          "options": [
            {"label": "The 15th", "dueAt": "2026-09-15T10:30:00Z"},
            {"label": "The 18th", "dueAt": "2026-09-18T10:30:00Z"}
          ],
          "questions": [
            {"question": "Is it the 15th or the 18th?"},
            {"question": "Which office?", "kind": "detail", "costOfWrong": "low", "options": []}
          ]
        }
        """);

        var rows = json.GetProperty("clarifications");

        // Entry one supplied nothing but its text, so kind, costOfWrong AND the
        // option chips all come from the top level.
        Assert.Equal("date", rows[0].GetProperty("kind").GetString());
        Assert.Equal("high", rows[0].GetProperty("costOfWrong").GetString());
        Assert.Equal(2, rows[0].GetProperty("options").GetArrayLength());

        // Entry two overrode all three. `"options": []` is SUPPLIED-and-empty, not
        // omitted: inheriting the date chips onto a "which office?" question would
        // put two time buttons under a question about a building.
        Assert.Equal("detail", rows[1].GetProperty("kind").GetString());
        Assert.Equal("low", rows[1].GetProperty("costOfWrong").GetString());
        Assert.Empty(rows[1].GetProperty("options").EnumerateArray());

        // The guess still comes off the top-level options, and it is still withheld.
        Assert.Equal("2026-09-15T10:30:00.000Z", json.GetProperty("task").GetProperty("dueAt").GetString());
        Assert.Equal("list", json.GetProperty("task").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task one_expensive_gap_withholds_the_reminder_for_the_whole_matter()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Pay the rent",
          "domain": "finance",
          "question": "Which day is it due?",
          "kind": "date",
          "costOfWrong": "low",
          "dueAtGuess": "2026-09-01T09:00:00+03:00",
          "questions": [
            {"question": "Which day is it due?", "costOfWrong": "high"},
            {"question": "Morning or evening nudge?", "kind": "choice", "costOfWrong": "low", "options": []}
          ]
        }
        """);

        // A matter is only as safe as its riskiest open gap. A 'low' sibling cannot
        // license a guessed date to fire at the user.
        Assert.Equal("list", json.GetProperty("task").GetProperty("kind").GetString());
        Assert.Empty(json.GetProperty("task").GetProperty("reminders").EnumerateArray());
        Assert.Equal("high", json.GetProperty("clarifications")[0].GetProperty("costOfWrong").GetString());
        Assert.Equal("low", json.GetProperty("clarifications")[1].GetProperty("costOfWrong").GetString());
    }

    [Fact]
    public async Task a_legacy_single_question_payload_answers_exactly_as_it_used_to()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // The wire format the tool has always sent. `questions` is additive: absent,
        // nothing about this response may move.
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

        var clarification = json.GetProperty("clarification");
        Assert.Equal("list", json.GetProperty("task").GetProperty("kind").GetString());
        Assert.Equal("high", clarification.GetProperty("costOfWrong").GetString());
        Assert.False(json.GetProperty("queueFull").GetBoolean());
        Assert.Equal("2026-08-12T06:00:00.000Z", json.GetProperty("task").GetProperty("dueAt").GetString());

        // The one added key is `clarifications`, and it holds the SAME row —
        // byte-identical, not a re-serialisation that quietly drops or renames a
        // field. A reader of either key sees the same object.
        var rows = json.GetProperty("clarifications");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(clarification.GetRawText(), rows[0].GetRawText());
    }

    // ---- The queue cap -----------------------------------------------------

    [Fact]
    public async Task the_cap_counts_rows_so_a_two_question_hold_spends_two_slots()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // One slot left. A hold asking two things gets to ask ONE of them —
        // `queueFull` still means "no question was filed at all", so it stays false
        // while any row was written and the caller compares lengths to see the rest.
        await Clarifications(db).InsertManyAsync(
            Enumerable.Range(0, ClarificationHoldService.MaxOpenClarifications - 1)
                .Select(i => Existing(userId, i, deferred: false)));

        var json = await PostCreatedAsync(userId, """
        {
          "title": "Go visit my friend",
          "domain": "family",
          "question": "What time should I remind you?",
          "kind": "date",
          "questions": [
            {
              "question": "What time should I remind you?",
              "kind": "date",
              "options": [
                {"label": "9 am", "dueAt": "2026-08-17T09:00:00Z"},
                {"label": "6 pm", "dueAt": "2026-08-17T18:00:00Z"}
              ]
            },
            {"question": "Which friend?", "kind": "detail", "options": []}
          ]
        }
        """);

        Assert.False(json.GetProperty("queueFull").GetBoolean());
        Assert.Equal(1, json.GetProperty("clarifications").GetArrayLength());
        Assert.Equal(
            "What time should I remind you?",
            json.GetProperty("clarification").GetProperty("question").GetString());

        Assert.Equal(
            (long)ClarificationHoldService.MaxOpenClarifications,
            await Clarifications(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    [Fact]
    public async Task the_last_slot_goes_to_the_date_question_wherever_it_sat()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        await Clarifications(db).InsertManyAsync(
            Enumerable.Range(0, ClarificationHoldService.MaxOpenClarifications - 1)
                .Select(i => Existing(userId, i, deferred: false)));

        // One slot, and the DETAIL gap is listed first. Truncating in array order
        // would keep it and drop the date — and the date question is the only one
        // that can promote the task, so the reminder would be withheld forever with
        // nothing left to ask that could release it.
        var json = await PostCreatedAsync(userId, """
        {
          "title": "Renew the passport",
          "domain": "home",
          "question": "Which office?",
          "kind": "detail",
          "questions": [
            {"question": "Which office?", "kind": "detail", "options": []},
            {
              "question": "Is it the 15th or the 18th?",
              "kind": "date",
              "options": [{"label": "The 15th", "dueAt": "2026-09-15T10:30:00Z"}]
            }
          ]
        }
        """);

        var rows = json.GetProperty("clarifications");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("date", rows[0].GetProperty("kind").GetString());
        Assert.Equal("Is it the 15th or the 18th?", rows[0].GetProperty("question").GetString());
    }

    [Fact]
    public async Task with_room_for_everything_the_asked_order_is_the_order_given()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // Prioritising is a TRUNCATION rule, not a sort. With room for both, a
        // deliberately detail-first hold stays detail-first — the card stack reads
        // them in the order they were asked.
        var json = await PostCreatedAsync(userId, """
        {
          "title": "Renew the passport",
          "domain": "home",
          "question": "Which office?",
          "kind": "detail",
          "questions": [
            {"question": "Which office?", "kind": "detail", "options": []},
            {
              "question": "Is it the 15th or the 18th?",
              "kind": "date",
              "options": [
                {"label": "The 15th", "dueAt": "2026-09-15T10:30:00Z"},
                {"label": "The 18th", "dueAt": "2026-09-18T10:30:00Z"}
              ]
            }
          ]
        }
        """);

        var rows = json.GetProperty("clarifications");

        // Three, not two: "Renew the passport" matches MoneyWords, and a renewal
        // costs money every time. The model asked about neither the office nor the
        // figure in one breath, so the figure is the server's question — appended,
        // which is why the two the model DID ask keep their order.
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("Which office?", rows[0].GetProperty("question").GetString());
        Assert.Equal("Is it the 15th or the 18th?", rows[1].GetProperty("question").GetString());
        Assert.Equal("How much is “Renew the passport”?", rows[2].GetProperty("question").GetString());
    }

    [Fact]
    public async Task a_dropped_expensive_question_still_withholds_and_the_date_still_releases()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        await Clarifications(db).InsertManyAsync(
            Enumerable.Range(0, ClarificationHoldService.MaxOpenClarifications - 1)
                .Select(i => Existing(userId, i, deferred: false)));

        // One slot, and the expensive question is the one that gets dropped. The
        // task's kind is decided over EVERY question the caller asked, filed or not:
        // a gap nobody will ever be asked about is unresolved forever, so it is the
        // last thing that should license a guessed date to fire.
        var json = await PostCreatedAsync(userId, """
        {
          "title": "Pay the rent",
          "domain": "finance",
          "question": "Which day is it due?",
          "kind": "date",
          "costOfWrong": "low",
          "timezone": "Africa/Cairo",
          "questions": [
            {
              "question": "Which day is it due?",
              "kind": "date",
              "costOfWrong": "low",
              "options": [{"label": "The 1st", "dueAt": "2026-09-01T09:00:00+03:00"}]
            },
            {"question": "Morning or evening nudge?", "kind": "choice", "costOfWrong": "high", "options": []}
          ]
        }
        """);

        var rows = json.GetProperty("clarifications");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("Which day is it due?", rows[0].GetProperty("question").GetString());
        Assert.Equal("list", json.GetProperty("task").GetProperty("kind").GetString());

        // ...and it is NOT stranded. The date question survived the truncation, and
        // answering it promotes the task exactly as it would have with no cap in
        // sight. Withholding is a delay, never a dead end.
        var resolved = await ResolveAsync(userId, rows[0].GetProperty("id").GetString()!, index: 0);

        Assert.Equal("reminder", resolved.GetProperty("task").GetProperty("kind").GetString());
        Assert.Equal("2026-09-01T06:00:00.000Z", resolved.GetProperty("task").GetProperty("dueAt").GetString());
        Assert.NotEmpty(resolved.GetProperty("task").GetProperty("reminders").EnumerateArray());
    }

    [Fact]
    public async Task past_the_cap_a_multi_question_hold_files_the_task_and_no_rows()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        await Clarifications(db).InsertManyAsync(
            Enumerable.Range(0, ClarificationHoldService.MaxOpenClarifications)
                .Select(i => Existing(userId, i, deferred: false)));

        var json = await PostCreatedAsync(userId, """
        {
          "title": "One too many",
          "domain": "home",
          "question": "Will this be asked?",
          "kind": "detail",
          "questions": [
            {"question": "Will this be asked?", "kind": "detail"},
            {"question": "Or this one?", "kind": "detail"}
          ]
        }
        """);

        Assert.True(json.GetProperty("queueFull").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("clarification").ValueKind);
        Assert.Empty(json.GetProperty("clarifications").EnumerateArray());
        Assert.Equal("One too many", json.GetProperty("task").GetProperty("title").GetString());

        Assert.Equal(
            (long)ClarificationHoldService.MaxOpenClarifications,
            await Clarifications(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

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
    public async Task a_date_question_with_nothing_to_tap_is_filed_with_chips_the_server_resolved()
    {
        // The 2026-08-23 loss, and the reason this stopped being a 400. The binder
        // used to reject this so the tool could hand the message back and the agent
        // re-call with options. It usually did. When it did not, `Parse` threw before
        // the service ran, no task was written, and the chat said "لم أتمكن من إضافة
        // المهمة" about a matter that had never existed. Six dateless sentences,
        // six losses. A missing date is a FACT about the matter, so the chips are
        // generated here rather than demanded from a model that cannot be relied on
        // to supply them.
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "When?", "kind": "date"
        }
        """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("task").GetProperty("id").GetString()));

        var date = body.GetProperty("clarifications").EnumerateArray()
            .Single(c => c.GetProperty("kind").GetString() == "date");

        // The model keeps its own wording — it is in the language of the message it
        // is answering. Only the answers are the server's.
        Assert.Equal("When?", date.GetProperty("question").GetString());
        Assert.NotEmpty(date.GetProperty("options").EnumerateArray());
    }

    [Fact]
    public async Task a_date_question_with_a_guess_and_no_options_is_still_allowed()
    {
        // The guess IS the concrete thing to correct — it rides in the card's facts
        // block. Only the case with neither is unanswerable.
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "When?", "kind": "date",
          "dueAtGuess": "2026-09-15T10:30:00Z"
        }
        """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task an_undated_chip_rides_along_rather_than_failing_the_whole_hold()
    {
        // This used to be a 400, on the reasoning that a chip with no dueAt sets
        // nothing when tapped. But "No date needed" is an undated chip the SERVER's
        // own generator emits — VoiceAutoFilePolicy.AskWhenItIsDue offers it first —
        // so the shape is legitimate and the voice lane files it daily. Refusing the
        // hold cost the user the matter to spare them one weak chip.
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "When?", "kind": "date",
          "options": [{"label": "The 15th", "dueAt": "2026-09-15T10:30:00Z"}, {"label": "Soon"}]
        }
        """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task a_bill_with_no_date_is_saved_and_asked_about_the_date_AND_the_money()
    {
        // "عايز ادفع فاتورة الغاز" — no date, no figure. The model holds for the date
        // and volunteers nothing about money, because the sentence never named a
        // number. Both questions are the server's to raise: NeedsAnAmount keys on the
        // title as well as the domain, which is how a bill filed under `home` and a
        // renewal filed under `car` are still asked what they cost.
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "دفع فاتورة الغاز", "domain": "home", "question": "إمتى؟", "kind": "date"
        }
        """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadJsonAsync(response);
        var rows = body.GetProperty("clarifications").EnumerateArray().ToList();
        var kinds = rows.Select(c => c.GetProperty("kind").GetString()).ToList();

        Assert.Contains("date", kinds);
        Assert.Contains("detail", kinds);

        // Server-composed, and composed in the language of the matter's own title.
        // No key: a key would send the client back to the app's language, which is
        // how an Arabic conversation got an English question under an Arabic one.
        var money = rows.Single(c => c.GetProperty("kind").GetString() == "detail");
        Assert.Equal("كم قيمة «دفع فاتورة الغاز»؟", money.GetProperty("question").GetString());
        Assert.False(money.TryGetProperty("questionKey", out var key) && key.ValueKind != JsonValueKind.Null);

        // And so are the chips on the date question the model left empty.
        var date = rows.Single(c => c.GetProperty("kind").GetString() == "date");
        var chips = date.GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("label").GetString()).ToList();
        Assert.Contains("لا حاجة لموعد", chips);

        // 09:00 Cairo, rendered the way Intl renders it on the client — and with an
        // upper-case meridiem, which ICU does not give en-GB on Linux by itself.
        Assert.Contains(chips, c => c is not null && c.Contains("9:00 ص", StringComparison.Ordinal));
    }

    [Fact]
    public async Task the_server_does_not_ask_for_money_the_model_already_asked_for()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await ResetAsync(db);

        // The card from 2026-08-24: "ما هو مبلغ الفاتورة؟" from the model and
        // "How much is …?" from the server, one above the other. The prompt still
        // tells the agent to put the figure in secondary_question and it obeyed;
        // adding ours on top asked the same gap twice, in two languages.
        var json = await PostCreatedAsync(userId, """
        {
          "title": "دفع فاتورة النت",
          "domain": "finance",
          "question": "متى تود دفع فاتورة النت؟",
          "kind": "date",
          "questions": [
            {"question": "متى تود دفع فاتورة النت؟", "kind": "date", "options": []},
            {"question": "ما هو مبلغ الفاتورة؟", "kind": "detail", "options": []}
          ]
        }
        """);

        var rows = json.GetProperty("clarifications").EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.GetProperty("question").GetString()!.Contains("كم قيمة"));

        // The date question still gets the chips it had nothing to tap without.
        var date = rows.Single(r => r.GetProperty("kind").GetString() == "date");
        Assert.NotEmpty(date.GetProperty("options").EnumerateArray());
    }

    [Fact]
    public async Task a_secondary_question_that_repeats_the_primary_is_rejected()
    {
        // One gap asked twice. The user answers the first card; the second can never be
        // closed by anything, so it sits in the queue occupying a slot forever.
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "family", "question": "When should I remind you?", "kind": "date",
          "questions": [
            {
              "question": "When should I remind you?",
              "kind": "date",
              "options": [{"label": "9 am", "dueAt": "2026-09-15T09:00:00Z"}]
            },
            {"question": "  when should i remind you?  ", "kind": "detail", "options": []}
          ]
        }
        """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var fields = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details")
            .GetProperty("fieldErrors");
        Assert.Equal(HoldBinder.DuplicateQuestion, fields.GetProperty("questions")[0].GetString());
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
    public async Task a_fourth_question_is_rejected()
    {
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "Q", "kind": "date",
          "questions": [{"question":"a"},{"question":"b"},{"question":"c"},{"question":"d"}]
        }
        """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Three is the ceiling because the open-queue cap counts every row: a
        // generous limit spends a user's whole question queue on one matter.
        var fields = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details")
            .GetProperty("fieldErrors");
        Assert.Equal(
            $"Array must contain at most {HoldBinder.MaxQuestions} element(s)",
            fields.GetProperty("questions")[0].GetString());
    }

    [Fact]
    public async Task a_questions_entry_reports_its_issues_under_the_top_level_name()
    {
        var response = await PostAsync(ObjectId.GenerateNewId(), """
        {
          "title": "T", "domain": "home", "question": "Q", "kind": "date",
          "questions": [{"kind": "colour"}]
        }
        """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // zod's flatten() buckets by issue.path[0], so a nested problem is filed at
        // "questions" — never "questions.0.kind".
        var fields = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details")
            .GetProperty("fieldErrors")
            .GetProperty("questions")
            .EnumerateArray()
            .Select(m => m.GetString())
            .ToArray();

        Assert.Contains("Required", fields);
        Assert.Contains(fields, m => m!.Contains("colour", StringComparison.Ordinal));
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
          "title": "T", "domain": "home", "question": "Q", "kind": "detail",
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

    /// <summary>Tap the option at <paramref name="index"/> on one held question.</summary>
    private async Task<JsonElement> ResolveAsync(ObjectId userId, string clarificationId, int index)
    {
        var response = await AuthedClient(userId).PostAsync(
            $"/me/clarifications/{clarificationId}/resolve",
            JsonBody($$$"""{"answer":{"type":"option","index":{{{index}}}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
