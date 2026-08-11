using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Xunit.Sdk;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <b>Proof that the live smoke assertions can go red.</b>
///
/// <para>
/// A green live run only means something if the thing it ran would have FAILED had the
/// defect been present. Every assertion in <see cref="LangflowTurnInvariants"/> is
/// therefore fed, here, the exact shipped defect it was written for — the wrapped tool
/// result, the seven frames for one call, the resolved gate, the offset-free time — and
/// is required to throw. An assertion that cannot fail is worse than no assertion,
/// because it converts "unchecked" into "checked and fine".
/// </para>
///
/// <para>
/// Offline and instant: these supply their own frames on purpose. That is exactly what
/// disqualifies them as coverage of Langflow and exactly what qualifies them as coverage
/// of the checker.
/// </para>
/// </summary>
public sealed class LangflowTurnInvariantsTests
{
    // ---- 1. the tool result the card reads ----------------------------------

    [Fact]
    public void accepts_the_unwrapped_create_result_that_ships_today()
    {
        var events = new[]
        {
            Call("call-1", "createTask"),
            Result("call-1", CreatedTask),
        };

        Assert.Equal(
            "6a7a6dc8522aceb15af17b33",
            LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(events, "createTask"));
    }

    [Fact]
    public void rejects_a_create_result_still_buried_under_langflows_content_wrapper()
    {
        // The shipped defect, captured live: Langflow wraps a tool's return in
        // `content` and our HTTP tool wraps its body in `value`, both as JSON strings.
        // The reply still said "Added it"; the card rendered a bare ledger row.
        var events = new[]
        {
            Call("call-1", "createTask"),
            Result("call-1", """{"content":"{\"value\":\"{\\\"ok\\\":true,\\\"task\\\":{\\\"id\\\":\\\"x\\\"}}\"}","status":"success"}"""),
        };

        Fails(() => LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(events, "createTask"), "content");
    }

    [Fact]
    public void rejects_a_create_result_with_no_task_on_it_at_all()
    {
        var events = new[]
        {
            Call("call-1", "createTask"),
            Result("call-1", """{"ok":true}"""),
        };

        Fails(() => LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(events, "createTask"), "task");
    }

    [Fact]
    public void rejects_a_turn_that_announced_the_create_and_never_resolved_it()
    {
        var events = new[] { Call("call-1", "createTask") };

        Fails(
            () => LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(events, "createTask"),
            "no tool_result");
    }

    [Fact]
    public void rejects_a_claimed_matter_the_store_has_never_heard_of()
    {
        var store = new Dictionary<string, DateTime?> { ["a-real-id"] = null };

        Fails(() => LangflowTurnInvariants.AssertClaimedMatterIsInTheStore("invented-id", store), "invented-id");
    }

    // ---- 2. the envelope must not reach the bubble --------------------------

    [Fact]
    public void accepts_tokens_that_are_ordinary_prose()
    {
        var events = new[] { Token("Added it — "), Token("1 September at 3 PM.") };

        Assert.Equal(
            "Added it — 1 September at 3 PM.",
            LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(events));
    }

    [Fact]
    public void rejects_an_envelope_leak_even_when_it_is_split_across_token_frames()
    {
        // The reason the check runs on the CONCATENATION. Langflow chunks tokens
        // wherever it likes, so no single frame contains the marker.
        var events = new[] { Token("{\"mo"), Token("de\": \"chat\", \"reply\": \"Added it\"}") };

        Fails(
            () => LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(events),
            "leaked into the chat bubble");
    }

    [Fact]
    public void rejects_an_envelope_key_that_reaches_the_bubble_without_its_opening_brace()
    {
        var events = new[] { Token("Here you go. \"pendingConfirmations\": [{\"tool\":\"deleteAllTasks\"}]") };

        Fails(() => LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(events), "envelope key");
    }

    [Fact]
    public void rejects_a_reply_printed_inside_a_code_fence()
    {
        var events = new[] { Token("```json\n{\"reply\"") };

        Fails(() => LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(events), "code fence");
    }

    [Fact]
    public void rejects_a_turn_with_no_prose_at_all_rather_than_passing_it_vacuously()
    {
        // An envelope reader that swallowed everything looks exactly like this, and
        // "contains no leak" would be trivially true.
        var events = new[] { Call("call-1", "queryTasks") };

        Fails(() => LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(events), "no token frame");
    }

    // ---- 3. one frame per invocation ----------------------------------------

    [Fact]
    public void accepts_two_genuinely_different_invocations()
    {
        var events = new[]
        {
            Call("call-1", "queryTasks", """{"domain":"car"}"""),
            Call("call-2", "queryTasks", """{"domain":"home"}"""),
        };

        LangflowTurnInvariants.AssertOneFramePerToolInvocation(events);
    }

    [Fact]
    public void rejects_the_redelivery_that_minted_seven_ids_for_one_call()
    {
        // The shipped defect exactly: Langflow re-sends the same add_message row as it
        // fills in, its tool_use blocks carry no id, and an id minted per delivery is
        // DISTINCT every time — so the distinct-id check in AssertTurnShape passes and
        // the user still sees seven pills for one query.
        var events = Enumerable
            .Range(0, 7)
            .Select(i => Call($"redelivery-{i}", "queryTasks", """{"status":"open"}"""))
            .ToArray();

        LangflowTurnInvariants.AssertTurnShape(WholeTurn(events));

        Fails(() => LangflowTurnInvariants.AssertOneFramePerToolInvocation(events), "more than once");
    }

    [Fact]
    public void rejects_seven_announced_calls_against_one_row_in_the_store()
    {
        var events = Enumerable
            .Range(0, 7)
            .Select(i => Call($"redelivery-{i}", "createTask", """{"title":"call the dentist"}"""))
            .ToArray();

        Fails(
            () => LangflowTurnInvariants.AssertToolCallCountMatchesSideEffects(events, "createTask", 1),
            "the database recorded");
    }

    [Fact]
    public void accepts_announcements_that_match_what_the_store_recorded()
    {
        var events = new[] { Call("call-1", "createTask") };

        LangflowTurnInvariants.AssertToolCallCountMatchesSideEffects(events, "createTask", 1);
    }

    // ---- 4. confirmation gating ---------------------------------------------

    [Fact]
    public void accepts_a_wipe_that_was_staged_and_changed_nothing()
    {
        Assert.Equal(
            "gated-1",
            LangflowTurnInvariants.AssertGatedCallIsStagedNotRun(
                new[] { Gated("gated-1") }, Pending("gated-1"), 2, 2));
    }

    [Fact]
    public void rejects_a_gated_call_the_stream_resolved_by_itself()
    {
        // Langflow runs deleteAllTasks' DRY RUN and redelivers the row with `output`
        // populated inside the same turn. Reading that preview as an outcome flipped
        // the stored record to `executed` and 404'd every confirmation.
        var events = new[]
        {
            Gated("gated-1"),
            Result("gated-1", """{"executed":false,"requiresConfirmation":true}"""),
        };

        Fails(() => LangflowTurnInvariants.AssertGatedCallIsStagedNotRun(events, Pending("gated-1"), 2, 2));
    }

    [Fact]
    public void rejects_a_gated_call_stored_as_anything_the_confirm_route_cannot_action()
    {
        Fails(
            () => LangflowTurnInvariants.AssertGatedCallIsStagedNotRun(
                new[] { Gated("gated-1") },
                new Dictionary<string, string> { ["gated-1"] = "executed" },
                2,
                2),
            "can never be actioned");
    }

    [Fact]
    public void rejects_a_wipe_that_deleted_before_anyone_confirmed_it()
    {
        Fails(
            () => LangflowTurnInvariants.AssertGatedCallIsStagedNotRun(
                new[] { Gated("gated-1") }, Pending("gated-1"), 2, 0),
            "BEFORE the user confirmed");
    }

    [Fact]
    public void accepts_a_confirm_that_really_removed_rows()
    {
        LangflowTurnInvariants.AssertConfirmedCallActuallyRan(
            new[] { Result("gated-1", """{"deleted":true,"deletedCount":2}""") },
            "gated-1",
            new Dictionary<string, string> { ["gated-1"] = "executed" },
            2,
            0);
    }

    [Fact]
    public void rejects_a_confirm_that_reported_success_while_the_rows_survived()
    {
        Fails(
            () => LangflowTurnInvariants.AssertConfirmedCallActuallyRan(
                new[] { Result("gated-1", """{"deleted":true,"deletedCount":2}""") },
                "gated-1",
                new Dictionary<string, string> { ["gated-1"] = "executed" },
                2,
                2),
            "did not move");
    }

    [Fact]
    public void rejects_a_confirm_the_route_refused_outright()
    {
        // Reproduced live: with the stream's gating removed, the record was flipped to
        // `executed` by Langflow's dry-run preview and the confirm route answered
        // 404 pending_call_not_found — "This confirmation has already been handled."
        Fails(
            () => LangflowTurnInvariants.AssertConfirmedCallActuallyRan(
                Array.Empty<AiStreamEvent>(),
                "gated-1",
                new Dictionary<string, string> { ["gated-1"] = "executed" },
                2,
                2,
                """404 {"error":{"code":"pending_call_not_found"}}"""),
            "never streamed");
    }

    [Fact]
    public void rejects_a_confirm_that_left_the_card_pressable_again()
    {
        Fails(
            () => LangflowTurnInvariants.AssertConfirmedCallActuallyRan(
                new[] { Result("gated-1", """{"deleted":true}""") },
                "gated-1",
                Pending("gated-1"),
                2,
                0),
            "pressed again");
    }

    // ---- 5. the time the user said ------------------------------------------

    [Fact]
    public void accepts_a_stated_time_that_round_tripped()
    {
        LangflowTurnInvariants.AssertStatedTimeRoundTrips(
            new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            "1 September 2026 at 3pm, Africa/Cairo");
    }

    [Fact]
    public void rejects_the_whole_offset_error_an_offset_free_current_date_produces()
    {
        // 15:00 Cairo stored as 15:00Z rather than 12:00Z — the silent failure when
        // `currentDate` carries no offset and the agent invents +00:00.
        Fails(
            () => LangflowTurnInvariants.AssertStatedTimeRoundTrips(
                new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc),
                "1 September 2026 at 3pm, Africa/Cairo"),
            "whole-offset error");
    }

    [Fact]
    public void rejects_a_time_that_was_silently_defaulted_away()
    {
        Fails(
            () => LangflowTurnInvariants.AssertStatedTimeRoundTrips(
                new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                null,
                "1 September 2026 at 3pm, Africa/Cairo"),
            "no dueAt at all");
    }

    // ---- the same assertions, over SSE-shaped payloads -----------------------

    [Fact]
    public void reads_json_element_payloads_the_same_way_it_reads_clr_ones()
    {
        // The confirm stream only exists over HTTP, so its frames come back as
        // JsonElements rather than CLR values. Both must reach the same verdict, or
        // half the gating story could never be checked at all.
        var wire = """
            data: {"type":"tool_call","callId":"gated-1","name":"deleteAllTasks","args":{},"needsConfirmation":true}
            data: {"type":"tool_result","callId":"other","result":{"deleted":true},"error":null}
            """;

        var events = wire
            .Split('\n')
            .Where(line => line.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line.Trim()["data:".Length..].Trim()).RootElement.Clone())
            .Select(frame => new AiStreamEvent(
                frame.GetProperty("type").GetString()!,
                frame.EnumerateObject()
                    .Where(p => p.Name != "type")
                    .ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal)))
            .ToList();

        Assert.Equal(
            "gated-1",
            LangflowTurnInvariants.AssertGatedCallIsStagedNotRun(events, Pending("gated-1"), 2, 2));
    }

    // ---- builders ------------------------------------------------------------

    /// <summary>A real <c>createTask</c> result, copied off the live stream.</summary>
    private const string CreatedTask =
        """{"ok":true,"task":{"id":"6a7a6dc8522aceb15af17b33","title":"call the dentist","domain":"health","kind":"reminder","status":"open","priority":"normal","dueAt":"2026-08-12T12:00:00.000Z","tags":[]}}""";

    private static AiStreamEvent Call(string callId, string name, string args = "{}") =>
        AiStreamEvents.ToolCall(callId, name, Json(args), needsConfirmation: false);

    private static AiStreamEvent Gated(string callId) =>
        AiStreamEvents.ToolCall(callId, AiToolCatalog.DeleteAllTasks, Json("""{"domain":"car"}"""), true);

    private static AiStreamEvent Result(string callId, string result) =>
        AiStreamEvents.ToolResult(callId, Json(result), null);

    private static AiStreamEvent Token(string text) => AiStreamEvents.Token(text);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>Wraps frames in the opening and closing the shape check requires.</summary>
    private static IReadOnlyList<AiStreamEvent> WholeTurn(IEnumerable<AiStreamEvent> middle) =>
        new[] { AiStreamEvents.Sources(Array.Empty<AiStreamSource>()) }
            .Concat(middle)
            .Append(AiStreamEvents.Done())
            .ToList();

    private static IReadOnlyDictionary<string, string> Pending(string callId) =>
        new Dictionary<string, string> { [callId] = AiConversationVocabulary.PendingConfirmation };

    /// <summary>
    /// The point of this whole file: the assertion must THROW, and its message must
    /// name the defect. A checker that fails with an unrelated message sends the next
    /// reader to the wrong place.
    /// </summary>
    private static void Fails(Action assertion, string? mentioning = null)
    {
        var failure = Record.Exception(assertion);

        Assert.True(failure is XunitException, "the invariant accepted a turn carrying the defect it exists to catch.");

        if (mentioning is not null)
        {
            Assert.Contains(mentioning, failure!.Message, StringComparison.Ordinal);
        }
    }
}
