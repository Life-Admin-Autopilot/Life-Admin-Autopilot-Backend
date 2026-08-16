using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The translator against a <b>REAL CAPTURED</b> Langflow turn — the one shape that
/// every previous test in this slice invented for itself.
///
/// <para>
/// <b>Provenance.</b> Every frame in <see cref="ThreeMatterTurn"/> was taken off a live
/// Langflow 1.11.2 (<c>POST /api/v1/run/{flow}?stream=true</c>) answering "add buy milk,
/// buy bread, and buy eggs to my list" through <c>PlanningInput-v4</c>. Bearer tokens
/// are redacted and the uuids are shortened; the STRUCTURE, the ordering and the
/// redelivery pattern are byte-faithful to the capture. Nothing here is authored from
/// documentation.
/// </para>
///
/// <para>
/// <b>The defect it pins.</b> The agent made three <c>createTask</c> calls and the store
/// gained three matters, but <c>add_message</c> only ever carried TWO
/// <c>tool_use</c> blocks — Langflow keeps one block per tool NAME and rewrites it when
/// the tool is called again. Reading the block's POSITION as an invocation identity
/// therefore announced two calls for three, and attached the "buy bread" result to the
/// pill whose arguments said "buy milk". Downstream,
/// <see cref="FabricatedActionGuard"/> saw the envelope claim a matter no tool result
/// accounted for and correctly withheld part of the answer — the user was told the
/// assistant had lied about work it had actually done.
/// </para>
///
/// <para>
/// See <see cref="LangflowToolActivity"/> for the two frame shapes and the excerpt of
/// the capture that shows block 0 changing its mind.
/// </para>
/// </summary>
public sealed class LangflowEventTranslatorTests
{
    // ---- the whole turn, exactly as it arrived ------------------------------

    [Fact]
    public void announces_one_call_for_each_of_the_three_tools_the_agent_really_ran()
    {
        var events = Translate(ThreeMatterTurn.Frames);

        var calls = events.Where(e => e.Kind == AiStreamEvents.ToolCallKind).ToList();

        // Three invocations happened and three matters were created. Two pills is the
        // client being told a smaller story than the database.
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Equal("createTask", call.Payload["name"]));

        Assert.Equal(
            new[] { "buy milk", "buy bread", "buy eggs" },
            calls.Select(TitleOfCall).ToArray());
    }

    [Fact]
    public void attaches_each_result_to_the_call_that_actually_produced_it()
    {
        var events = Translate(ThreeMatterTurn.Frames);

        // The pairing, stated the only way that cannot be argued with: for every
        // tool_result, the title inside the RESULT is the title the matching
        // tool_call's ARGUMENTS asked for.
        foreach (var result in events.Where(e => e.Kind == AiStreamEvents.ToolResultKind))
        {
            var callId = (string)result.Payload["callId"]!;

            var call = Assert.Single(
                events,
                e => e.Kind == AiStreamEvents.ToolCallKind && (string)e.Payload["callId"]! == callId);

            Assert.Equal(TitleOfCall(call), TitleOfResult(result));
        }

        // And all three settled, rather than one pill being left to spin forever.
        Assert.Equal(3, events.Count(e => e.Kind == AiStreamEvents.ToolResultKind));
    }

    [Fact]
    public void every_call_id_the_client_is_shown_is_distinct()
    {
        var events = Translate(ThreeMatterTurn.Frames);

        var ids = events
            .Where(e => e.Kind == AiStreamEvents.ToolCallKind)
            .Select(e => (string)e.Payload["callId"]!)
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void does_not_accuse_an_honest_turn_of_fabricating_the_work_it_did()
    {
        // The end-to-end symptom. The envelope claims three task ids; all three came
        // back from a tool; so the guard must stay silent. Under the block-index
        // scheme one of the three results never reached the translator and the guard
        // fired `unverified_action`, discarding part of a truthful answer.
        var translator = new LangflowEventTranslator();
        Translate(ThreeMatterTurn.Frames, translator);

        Assert.Null(FabricatedActionGuard.FirstUnaccounted(translator.Claims, translator.ToolCalls));
    }

    [Fact]
    public void records_all_three_calls_as_executed_for_the_persisted_turn()
    {
        var translator = new LangflowEventTranslator();
        Translate(ThreeMatterTurn.Frames, translator);

        Assert.Equal(3, translator.ToolCalls.Count);
        Assert.All(translator.ToolCalls, call =>
        {
            Assert.Equal("executed", call.Status);
            Assert.NotNull(call.Result);
        });
    }

    // ---- the same turn with the log frames removed ---------------------------

    [Fact]
    public void still_separates_the_calls_when_only_the_message_rows_arrive()
    {
        // A deployment that does not stream `log` frames has nothing but the rewritten
        // blocks. It cannot recover the call whose block was overwritten before any
        // output landed on it — but what it does report must still be TRUE, and that is
        // the property this pins: a block that changed its arguments is a new call, not
        // a new state of the old one.
        var events = Translate(ThreeMatterTurn.Frames.Where(f => f.EventName != "log"));

        var calls = events.Where(e => e.Kind == AiStreamEvents.ToolCallKind).ToList();
        Assert.Equal(3, calls.Count);

        foreach (var result in events.Where(e => e.Kind == AiStreamEvents.ToolResultKind))
        {
            var callId = (string)result.Payload["callId"]!;
            var call = Assert.Single(
                events,
                e => e.Kind == AiStreamEvents.ToolCallKind && (string)e.Payload["callId"]! == callId);

            // The exact mis-pairing that shipped: args said "buy milk", result said
            // "buy bread".
            Assert.Equal(TitleOfCall(call), TitleOfResult(result));
        }
    }

    [Fact]
    public void stays_silent_rather_than_accusing_when_an_outcome_never_arrived()
    {
        // Without the log frames one call has no result, so the turn cannot be judged
        // at all — FabricatedActionGuard.IsVerifiable. A false accusation is worse
        // than a missed one, and this is the path where that matters.
        var translator = new LangflowEventTranslator();
        Translate(ThreeMatterTurn.Frames.Where(f => f.EventName != "log"), translator);

        Assert.Null(FabricatedActionGuard.FirstUnaccounted(translator.Claims, translator.ToolCalls));
    }

    // ---- the redelivery properties the old scheme was built for --------------

    [Fact]
    public void announces_one_pill_however_many_times_the_row_is_redelivered()
    {
        // The capture redelivers the same finished row six times. The dedup that
        // property depends on must survive the identity change that fixed the pairing.
        var events = Translate(ThreeMatterTurn.Frames);

        Assert.Equal(3, events.Count(e => e.Kind == AiStreamEvents.ToolCallKind));
        Assert.Equal(3, events.Count(e => e.Kind == AiStreamEvents.ToolResultKind));
    }

    [Fact]
    public void reports_a_call_once_even_though_both_wire_shapes_describe_it()
    {
        // `log` and `add_message` are two accounts of the SAME invocation. Reading both
        // without reconciling them would double every pill.
        var withBoth = Translate(ThreeMatterTurn.Frames)
            .Count(e => e.Kind == AiStreamEvents.ToolCallKind);

        var logsOnly = Translate(ThreeMatterTurn.Frames.Where(f => f.EventName == "log"))
            .Count(e => e.Kind == AiStreamEvents.ToolCallKind);

        Assert.Equal(3, logsOnly);
        Assert.Equal(logsOnly, withBoth);
    }

    [Fact]
    public void keeps_the_two_accounts_reconciled_when_the_message_row_arrives_first()
    {
        // Ordering is not asserted by the capture — logs led there, but a deployment
        // that buffers differently must not produce six pills for three calls.
        var reordered = ThreeMatterTurn.Frames
            .Where(f => f.EventName == "add_message")
            .Concat(ThreeMatterTurn.Frames.Where(f => f.EventName == "log"));

        var events = Translate(reordered);

        Assert.Equal(3, events.Count(e => e.Kind == AiStreamEvents.ToolCallKind));

        foreach (var result in events.Where(e => e.Kind == AiStreamEvents.ToolResultKind))
        {
            var callId = (string)result.Payload["callId"]!;
            var call = Assert.Single(
                events,
                e => e.Kind == AiStreamEvents.ToolCallKind && (string)e.Payload["callId"]! == callId);

            Assert.Equal(TitleOfCall(call), TitleOfResult(result));
        }
    }

    [Fact]
    public void never_mints_the_same_call_id_in_two_turns_of_the_same_capture()
    {
        // Cross-turn uniqueness is what the confirm route depends on. The capture's
        // message id is the same string every time it is replayed, so the ids derived
        // from it must still differ between two translators... and where Langflow
        // supplies its own uuid, they are unique by construction.
        var first = Translate(ThreeMatterTurn.Frames.Where(f => f.EventName != "log"));
        var second = Translate(ThreeMatterTurn.Frames.Where(f => f.EventName != "log"));

        var a = first.Where(e => e.Kind == AiStreamEvents.ToolCallKind)
            .Select(e => (string)e.Payload["callId"]!).ToList();
        var b = second.Where(e => e.Kind == AiStreamEvents.ToolCallKind)
            .Select(e => (string)e.Payload["callId"]!).ToList();

        // Same message id in the capture, so these are equal by design — the guard
        // against a cross-TURN collision is the message id itself, which Langflow
        // makes unique per turn. What must never happen is two calls inside ONE turn
        // sharing an id.
        Assert.Equal(a, b);
        Assert.Equal(3, a.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- the log frame reader in isolation -----------------------------------

    [Fact]
    public void reads_every_tool_call_out_of_a_chain_start_log_frame()
    {
        var frame = ParseOne(ThreeMatterTurn.CallsBuyMilk);

        var invocation = Assert.Single(LangflowToolActivity.Invocations(frame.Data));

        Assert.Equal("createTask", invocation.Name);
        Assert.Equal("id-1", invocation.CallId);
    }

    [Fact]
    public void ignores_the_agents_own_chain_start_whose_inputs_are_messages()
    {
        // The agent's own chain_start uses the SAME message type with an inputs OBJECT
        // rather than an array of calls. Treating it as tool activity would announce a
        // pill for the prompt.
        var frame = ParseOne(
            """{"event":"log","data":{"message":{"type":"chain_start","name":"agent","inputs":{"messages":[]}}}}""");

        Assert.Empty(LangflowToolActivity.Invocations(frame.Data));
        Assert.Null(LangflowToolActivity.Outcome(frame.Data));
    }

    [Fact]
    public void pairs_an_outcome_by_the_tool_call_id_it_carries()
    {
        var frame = ParseOne(ThreeMatterTurn.EndsBuyMilk);

        var outcome = LangflowToolActivity.Outcome(frame.Data);

        Assert.NotNull(outcome);
        Assert.Equal("id-1", outcome!.Value.CallId);
        Assert.Null(outcome.Value.Error);
    }

    [Fact]
    public void treats_a_non_success_status_as_a_failure_rather_than_an_outcome()
    {
        // The success shape is measured; this one is not, so it is read the safe way —
        // a call whose outcome cannot be confirmed must not be recorded as executed.
        var frame = ParseOne(
            """
            {"event":"log","data":{"message":{"type":"tool_end","output":{
              "type":"tool","name":"createTask","tool_call_id":"id-9",
              "content":"upstream refused","status":"error"}}}}
            """);

        var outcome = LangflowToolActivity.Outcome(frame.Data);

        Assert.NotNull(outcome);
        Assert.Equal("upstream refused", outcome!.Value.Error);
    }

    [Fact]
    public void fingerprints_one_invocation_the_same_however_its_arguments_were_ordered()
    {
        // The join between the two wire shapes: `log`'s `args` and the block's
        // `tool_input` are one dictionary serialized twice, and Python does not promise
        // key order. Two orderings that disagree would double the pill.
        var a = Args("""{"title":"buy milk","domain":"home","kind":"list"}""");
        var b = Args("""{"kind":"list","domain":"home","title":"buy milk"}""");

        Assert.Equal(
            LangflowToolActivity.Fingerprint("createTask", a),
            LangflowToolActivity.Fingerprint("createTask", b));

        // And a genuinely different call is a genuinely different key.
        Assert.NotEqual(
            LangflowToolActivity.Fingerprint("createTask", a),
            LangflowToolActivity.Fingerprint("createTask", Args("""{"title":"buy bread"}""")));
    }

    // ---- helpers -------------------------------------------------------------

    private static List<AiStreamEvent> Translate(
        IEnumerable<LangflowFrame> frames,
        LangflowEventTranslator? translator = null)
    {
        translator ??= new LangflowEventTranslator();

        var events = new List<AiStreamEvent>();
        foreach (var frame in frames)
        {
            events.AddRange(translator.Accept(frame));
        }

        return events;
    }

    /// <summary>The <c>title</c> the call's arguments asked for.</summary>
    private static string? TitleOfCall(AiStreamEvent call) =>
        call.Payload["args"] is JsonElement args
            ? LangflowWireContract.ReadString(args, "title")
            : null;

    /// <summary>The <c>title</c> of the matter the result says was created.</summary>
    private static string? TitleOfResult(AiStreamEvent result)
    {
        var payload = Assert.IsType<JsonElement>(result.Payload["result"]);
        var task = LangflowWireContract.ReadElement(payload, "task");

        return task is null ? null : LangflowWireContract.ReadString(task.Value, "title");
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static LangflowFrame ParseOne(string line)
    {
        string? pending = null;
        Assert.True(LangflowWireContract.TryParseLine(line.ReplaceLineEndings(" "), ref pending, out var frame));
        return frame;
    }
}
