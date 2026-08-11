using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.DAL.Features.Ai;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <b>The turn that says it filed something and filed nothing.</b>
///
/// <para>
/// Measured live on a fresh account: one reply of "Added it." with zero
/// <c>tool_call</c> frames and zero rows written; and separately, a complete
/// clarification written into the envelope under an invented <c>taskId</c>
/// (<c>hold_math_lec_2026-08-12</c>) with no tool called at all. Both leave the user
/// believing a thing exists that no screen in the product will ever show them.
/// </para>
///
/// <para>
/// The two halves of the contract these pin: the guard FIRES on a claim nothing
/// accounts for, and — the harder half — it stays SILENT on every legitimate turn,
/// including the plain-prose chat reply that no word matcher could tell apart from a
/// fabrication.
/// </para>
/// </summary>
public sealed class AiFabricatedActionTests
{
    // ---- reading the envelope's structured half ------------------------------

    [Fact]
    public void the_claims_are_the_ids_the_envelope_says_a_tool_returned()
    {
        var claims = PlanningEnvelopeClaims.Read("""
            {"mode":"chat","reply":"Filed.",
             "tasks":[{"id":"6a7a0e94b268388ede52790b","title":"passport"}],
             "clarifications":[{"taskId":"6a7a110a31340d339dab118c","question":"when?"}],
             "pendingConfirmations":[]}
            """);

        Assert.True(claims.Parsed);
        Assert.Equal(
            new[]
            {
                new EnvelopeClaim("tasks", "6a7a0e94b268388ede52790b"),
                new EnvelopeClaim("clarifications", "6a7a110a31340d339dab118c"),
            },
            claims.Claims);
    }

    [Theory]
    [InlineData("Sure — what time on Thursday?")]      // a prose flow, or an older one
    [InlineData("""{"mode":"chat","reply":"Filed.","tasks":[{"id":"6a7a""")]  // cut off mid-array
    [InlineData("")]
    [InlineData(null)]
    public void an_envelope_that_cannot_be_read_yields_no_verdict(string? output)
    {
        // Failing open is the whole design: a half-written array is exactly where a
        // guard starts inventing accusations, and a truncated turn already surfaces
        // its own error.
        Assert.False(PlanningEnvelopeClaims.Read(output).Parsed);
    }

    // ---- the guard fires -----------------------------------------------------

    [Fact]
    public void a_task_no_tool_ever_returned_is_unaccounted()
    {
        var verdict = FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Added it.","tasks":[{"id":"6a7a0e94b268388ede52790b"}],
                 "clarifications":[],"pendingConfirmations":[]}
                """),
            Array.Empty<TranslatedToolCall>());

        Assert.NotNull(verdict);
        Assert.Equal("tasks", verdict!.Value.Section);
        Assert.Equal("6a7a0e94b268388ede52790b", verdict.Value.Id);
    }

    [Fact]
    public void the_invented_clarification_id_that_was_measured_live_is_unaccounted()
    {
        var verdict = FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Filed.","tasks":[],
                 "clarifications":[{"taskId":"hold_math_lec_2026-08-12","question":"which lecture?"}],
                 "pendingConfirmations":[]}
                """),
            Array.Empty<TranslatedToolCall>());

        Assert.Equal("hold_math_lec_2026-08-12", verdict!.Value.Id);
    }

    [Fact]
    public void a_task_row_with_no_id_at_all_is_unaccounted()
    {
        // The flow's contract: an item with no tool-returned id "does not go in the
        // array at all". Its presence is the claim.
        var verdict = FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Done.","tasks":[{"title":"renew passport"}],
                 "clarifications":[],"pendingConfirmations":[]}
                """),
            Array.Empty<TranslatedToolCall>());

        Assert.NotNull(verdict);
        Assert.Null(verdict!.Value.Id);
    }

    [Fact]
    public void an_id_the_model_passed_INTO_a_tool_does_not_account_for_itself()
    {
        // Arguments are the model's own words. Only what came back counts, or an
        // invented id laundered through a tool call would validate itself.
        var verdict = FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Updated.","tasks":[{"id":"made-up-id"}],
                 "clarifications":[],"pendingConfirmations":[]}
                """),
            new[] { Call("updateTask", "executed", args: """{"id":"made-up-id"}""", result: """{"ok":false}""") });

        Assert.Equal("made-up-id", verdict!.Value.Id);
    }

    [Fact]
    public void a_confirmation_card_claimed_without_a_gated_call_is_unaccounted()
    {
        var verdict = FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Confirm?","tasks":[],"clarifications":[],
                 "pendingConfirmations":[{"tool":"deleteAllTasks"}]}
                """),
            Array.Empty<TranslatedToolCall>());

        // "Shall I delete everything? Confirm below" with no card below is a dead end
        // the user cannot act on.
        Assert.Equal("pendingConfirmations", verdict!.Value.Section);
    }

    // ---- the guard stays silent ----------------------------------------------

    [Fact]
    public void a_task_whose_id_the_tool_returned_is_accounted_for()
    {
        Assert.Null(FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Filed.","tasks":[{"id":"6a7a0e94b268388ede52790b"}],
                 "clarifications":[],"pendingConfirmations":[]}
                """),
            new[]
            {
                Call("createTask", "executed",
                    result: """{"ok":true,"task":{"id":"6a7a0e94b268388ede52790b","title":"passport"}}"""),
            }));
    }

    [Fact]
    public void an_id_nested_anywhere_in_the_tool_output_counts()
    {
        // Tool results are Mixed and every tool shapes its own: holdForClarification
        // buries the id one level down, and the next tool will bury it somewhere else.
        Assert.Null(FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Asked.","tasks":[],
                 "clarifications":[{"taskId":"6a7a110a31340d339dab118c"}],
                 "pendingConfirmations":[]}
                """),
            new[]
            {
                Call("holdForClarification", "executed",
                    result: """{"ok":true,"clarification":{"taskId":"6a7a110a31340d339dab118c","question":"when?"}}"""),
            }));
    }

    [Fact]
    public void ordinary_chat_that_claims_nothing_passes_however_it_is_worded()
    {
        // THE false positive to avoid. "Added it." with empty arrays is
        // indistinguishable from "Hi! How can I help?" without reading the words —
        // and the words are in the user's language. Neither claims anything
        // structurally, so neither is judged.
        foreach (var reply in new[] { "Hi! How can I help?", "Added it.", "أضفتها." })
        {
            Assert.Null(FabricatedActionGuard.FirstUnaccounted(
                PlanningEnvelopeClaims.Read(
                    $$"""{"mode":"chat","reply":{{JsonSerializer.Serialize(reply)}},"tasks":[],"clarifications":[],"pendingConfirmations":[]}"""),
                Array.Empty<TranslatedToolCall>()));
        }
    }

    [Fact]
    public void a_turn_whose_tool_outcome_never_arrived_is_not_judged()
    {
        // The translator marks a non-gated call `executed` the moment it is announced
        // and fills the result in when Langflow delivers one. No result means the ids
        // that call returned are unknown to us — and flagging a turn that did real
        // work is worse than missing one that did not.
        Assert.Null(FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Filed.","tasks":[{"id":"6a7a0e94b268388ede52790b"}],
                 "clarifications":[],"pendingConfirmations":[]}
                """),
            new[] { Call("createTask", "executed") }));
    }

    [Fact]
    public void a_gated_call_accounts_for_a_pending_confirmation_without_resolving()
    {
        // A gated call is unresolved BY DESIGN this turn — its result belongs to the
        // confirm route — so it must neither be judged unverifiable nor flagged.
        Assert.Null(FabricatedActionGuard.FirstUnaccounted(
            PlanningEnvelopeClaims.Read("""
                {"reply":"Delete all 12? Confirm below.","tasks":[],"clarifications":[],
                 "pendingConfirmations":[{"tool":"deleteAllTasks"}]}
                """),
            new[] { Call("deleteAllTasks", "pending_confirmation") }));
    }

    // ---- the whole turn, through the provider --------------------------------

    [Fact]
    public async Task a_fabricated_turn_is_reported_as_an_error_and_never_persisted()
    {
        var database = StubbedLangflow.Database("fabrication");
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(database);

        // The measured shape: a complete, well-formed envelope claiming a task, and
        // not one tool_use anywhere in the stream.
        var handler = StubbedLangflow.Handler(
            """{"event":"token","data":{"chunk":"{\"mode\":\"chat\",\"reply\":\"Added it.\",\"tasks\":[{\"id\":\"6a7a0e94b268388ede52790b\"}],"}}""",
            """{"event":"token","data":{"chunk":"\"clarifications\":[],\"pendingConfirmations\":[]}"}}""",
            """{"event":"end","data":{}}""");

        var events = await StubbedLangflow.AskAsync(
            StubbedLangflow.Provider(handler, database, repository), userId);

        var error = Assert.Single(events, e => e.Kind == AiStreamEvents.ErrorKind);
        Assert.Equal(FabricatedActionGuard.ErrorCode, error.Payload["code"]);

        // Before `done`, so the client sees it as part of this turn rather than after it.
        Assert.True(
            events.FindIndex(e => e.Kind == AiStreamEvents.ErrorKind)
            < events.FindIndex(e => e.Kind == AiStreamEvents.DoneKind));

        // And the history keeps the question but not the claim: reopening the chat
        // must not re-assert that something was filed.
        var messages = await repository.RecentTurnsAsync(userId, AiConversationVocabulary.PersonalScope, 10);
        Assert.Equal(new[] { "user" }, messages.Select(m => m.Role).ToArray());
    }

    [Fact]
    public async Task a_fabricated_turn_still_keeps_the_tool_calls_that_really_ran()
    {
        var database = StubbedLangflow.Database("fabrication");
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(database);

        // One real gated call, plus a task claimed out of thin air.
        var handler = StubbedLangflow.Handler(
            """{"event":"tool_call","data":{"callId":"call-gated","name":"deleteAllTasks","args":{}}}""",
            """{"event":"token","data":{"chunk":"{\"reply\":\"Confirm below. Also added one.\",\"tasks\":[{\"id\":\"invented\"}],"}}""",
            """{"event":"token","data":{"chunk":"\"clarifications\":[],\"pendingConfirmations\":[{\"tool\":\"deleteAllTasks\"}]}"}}""",
            """{"event":"end","data":{}}""");

        var events = await StubbedLangflow.AskAsync(
            StubbedLangflow.Provider(handler, database, repository), userId);

        Assert.Contains(events, e => e.Kind == AiStreamEvents.ErrorKind);

        // Dropping the record along with the prose would 404 the confirm button on
        // the card the user is looking at. What really happened stays.
        var call = await repository.FindToolCallAsync(
            userId, AiConversationVocabulary.PersonalScope, "call-gated");

        Assert.NotNull(call);
        Assert.Equal(AiConversationVocabulary.PendingConfirmation, call!.Status);

        var messages = await repository.RecentTurnsAsync(userId, AiConversationVocabulary.PersonalScope, 10);
        Assert.Equal(string.Empty, messages.Last(m => m.Role == "assistant").Text);
    }

    [Fact]
    public async Task an_honest_turn_streams_and_persists_exactly_as_before()
    {
        var database = StubbedLangflow.Database("fabrication");
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(database);

        var handler = StubbedLangflow.Handler(
            """{"event":"tool_call","data":{"callId":"call-1","name":"createTask","args":{"title":"passport"}}}""",
            """{"event":"tool_result","data":{"callId":"call-1","result":{"ok":true,"task":{"id":"6a7a0e94b268388ede52790b"}}}}""",
            """{"event":"token","data":{"chunk":"{\"reply\":\"Filed.\",\"tasks\":[{\"id\":\"6a7a0e94b268388ede52790b\"}],"}}""",
            """{"event":"token","data":{"chunk":"\"clarifications\":[],\"pendingConfirmations\":[]}"}}""",
            """{"event":"end","data":{}}""");

        var events = await StubbedLangflow.AskAsync(
            StubbedLangflow.Provider(handler, database, repository), userId);

        // The negative control for the whole feature: a genuine turn must be
        // untouched by the guard, prose and all.
        Assert.DoesNotContain(events, e => e.Kind == AiStreamEvents.ErrorKind);

        var messages = await repository.RecentTurnsAsync(userId, AiConversationVocabulary.PersonalScope, 10);
        Assert.Equal("Filed.", messages.Last(m => m.Role == "assistant").Text);
    }

    // ---- helpers -------------------------------------------------------------

    private static TranslatedToolCall Call(
        string name,
        string status,
        string? args = null,
        string? result = null) =>
        new(
            $"call-{name}",
            name,
            args is null ? null : JsonDocument.Parse(args).RootElement,
            status,
            result is null ? null : JsonDocument.Parse(result).RootElement);
}
