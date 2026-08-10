using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The Langflow → contract translation, against <b>synthetic</b> Langflow output.
///
/// <para>
/// <b>No Langflow instance was reachable</b> while this was written (nothing on
/// :7860), so every frame below is authored from Langflow's documented streaming
/// surface rather than captured from a run. That makes these tests a faithful
/// account of what the translator does with a given input, and NOT evidence that the
/// input is what Langflow really sends. The frames it produces, by contrast, ARE the
/// measured contract — those assertions are load-bearing.
/// </para>
/// </summary>
public sealed class LangflowTranslationTests
{
    // ---- the wire, both encodings ------------------------------------------

    [Fact]
    public void parses_ndjson_frames()
    {
        string? pending = null;

        var parsed = LangflowWireContract.TryParseLine(
            """{"event":"token","data":{"chunk":"Hel"}}""", ref pending, out var frame);

        Assert.True(parsed);
        Assert.Equal("token", frame.EventName);
        Assert.Equal("Hel", LangflowWireContract.ReadString(frame.Data, "chunk"));
    }

    [Fact]
    public void carries_an_sse_event_line_across_to_its_data_line()
    {
        string? pending = null;

        Assert.False(LangflowWireContract.TryParseLine("event: token", ref pending, out _));
        Assert.Equal("token", pending);

        var parsed = LangflowWireContract.TryParseLine(
            """data: {"chunk":"lo"}""", ref pending, out var frame);

        Assert.True(parsed);
        Assert.Equal("token", frame.EventName);
        Assert.Equal("lo", LangflowWireContract.ReadString(frame.Data, "chunk"));

        // Consumed — it must not leak onto the next data line.
        Assert.Null(pending);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(": langflow keep-alive")]
    [InlineData("data: [DONE]")]
    [InlineData("not json at all")]
    [InlineData("""{"event":"token","data":{"chunk":"tr""")]
    public void ignores_a_line_it_cannot_use_rather_than_failing_the_turn(string line)
    {
        // A blank line, someone else's keep-alive, or a truncated chunk must not
        // abort an answer that is otherwise fine.
        string? pending = null;

        Assert.False(LangflowWireContract.TryParseLine(line, ref pending, out _));
    }

    // ---- tokens -------------------------------------------------------------

    [Fact]
    public void streams_each_token_as_it_arrives()
    {
        var translator = new LangflowEventTranslator();

        var first = Single(translator.Accept(Frame("token", """{"chunk":"Book "}""")));
        var second = Single(translator.Accept(Frame("token", """{"chunk":"the vet."}""")));

        // Live, one frame per chunk — not buffered and flushed at the end. This is
        // the whole reason the streaming run endpoint is used.
        Assert.Equal(AiStreamEvents.TokenKind, first.Kind);
        Assert.Equal("Book ", first.Payload["text"]);
        Assert.Equal("the vet.", second.Payload["text"]);

        Assert.Equal("Book the vet.", translator.AssistantText);
    }

    [Fact]
    public void does_not_repeat_a_streamed_answer_as_the_end_frame_text()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(Frame("token", """{"chunk":"Done."}""")).ToList();
        translator.Accept(Frame("end", EndWithText("Done."))).ToList();

        // The `end` frame repeats the whole answer. Emitting it again would print
        // the reply twice in the chat.
        var tail = translator.Complete().ToList();

        Assert.Single(tail);
        Assert.Equal(AiStreamEvents.DoneKind, tail[0].Kind);
    }

    [Fact]
    public void falls_back_to_the_end_frame_text_when_nothing_streamed()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(Frame("end", EndWithText("All set."))).ToList();

        var tail = translator.Complete().ToList();

        // A flow with streaming switched off still has to produce an answer.
        Assert.Equal(2, tail.Count);
        Assert.Equal(AiStreamEvents.TokenKind, tail[0].Kind);
        Assert.Equal("All set.", tail[0].Payload["text"]);
        Assert.Equal(AiStreamEvents.DoneKind, tail[1].Kind);
    }

    // ---- tool activity ------------------------------------------------------

    [Fact]
    public void turns_a_tool_use_content_block_into_a_call_and_a_result()
    {
        var translator = new LangflowEventTranslator();

        var events = translator.Accept(Frame("add_message", """
            {
              "text": "",
              "content_blocks": [{
                "contents": [{
                  "type": "tool_use",
                  "id": "call-1",
                  "name": "SaveTaskTool",
                  "tool_input": {"title": "MOT", "domain": "car"},
                  "output": {"task": {"id": "abc", "title": "MOT"}}
                }]
              }]
            }
            """)).ToList();

        Assert.Equal(2, events.Count);

        Assert.Equal(AiStreamEvents.ToolCallKind, events[0].Kind);
        Assert.Equal("call-1", events[0].Payload["callId"]);

        // Langflow names the COMPONENT; the pill has to read as the action.
        Assert.Equal("createTask", events[0].Payload["name"]);
        Assert.Equal(false, events[0].Payload["needsConfirmation"]);

        Assert.Equal(AiStreamEvents.ToolResultKind, events[1].Kind);
        Assert.Equal("call-1", events[1].Payload["callId"]);
        Assert.Null(events[1].Payload["error"]);
        Assert.NotNull(events[1].Payload["result"]);
    }

    [Fact]
    public void announces_a_tool_once_even_when_the_message_is_resent()
    {
        var translator = new LangflowEventTranslator();

        // Langflow re-sends the same message row as it fills in: first the call,
        // then the same call with its output. The pill must not appear twice.
        var pending = translator.Accept(Frame("add_message", ToolUse("call-1", "queryTasks", output: null))).ToList();
        var settled = translator.Accept(Frame("add_message", ToolUse("call-1", "queryTasks", output: """{"count":3}"""))).ToList();

        Assert.Single(pending);
        Assert.Equal(AiStreamEvents.ToolCallKind, pending[0].Kind);

        Assert.Single(settled);
        Assert.Equal(AiStreamEvents.ToolResultKind, settled[0].Kind);

        // And a third delivery of the finished row adds nothing at all.
        Assert.Empty(translator.Accept(Frame("add_message", ToolUse("call-1", "queryTasks", output: """{"count":3}"""))));
    }

    [Fact]
    public void marks_only_delete_all_tasks_as_needing_confirmation()
    {
        foreach (var name in AiToolCatalog.ToolNames)
        {
            var translator = new LangflowEventTranslator();
            // Non-interpolated raw string plus Replace: the JSON's own `}}` runs
            // collide with $$-interpolation's closing delimiter, and bumping to $$$
            // just moves the collision to the `}}}` further along.
            var call = Single(translator.Accept(Frame(
                "tool_call",
                """{"callId":"c","name":"__NAME__","args":{}}""".Replace("__NAME__", name, StringComparison.Ordinal))));

            Assert.Equal(
                name == AiToolCatalog.DeleteAllTasks,
                (bool)call.Payload["needsConfirmation"]!);
        }
    }

    [Fact]
    public void records_a_confirmable_call_as_pending_and_an_inline_one_as_executed()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(Frame("tool_call", """{"callId":"c1","name":"deleteAllTasks","args":{}}""")).ToList();
        translator.Accept(Frame("tool_call", """{"callId":"c2","name":"queryTasks","args":{}}""")).ToList();

        // The status is what gets persisted, and only `pending_confirmation` can
        // later be confirmed.
        Assert.Equal("pending_confirmation", translator.ToolCalls[0].Status);
        Assert.Equal("executed", translator.ToolCalls[1].Status);
    }

    [Fact]
    public void reports_a_failed_tool_with_error_set_and_result_null()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(Frame("tool_call", """{"callId":"c","name":"updateTask","args":{}}""")).ToList();

        var result = Single(translator.Accept(
            Frame("tool_result", """{"callId":"c","error":"That matter could not be found."}""")));

        // BOTH keys present. The client branches on `error !== null`, so a missing
        // `result` key would read as a success.
        Assert.True(result.Payload.ContainsKey("result"));
        Assert.True(result.Payload.ContainsKey("error"));
        Assert.Null(result.Payload["result"]);
        Assert.Equal("That matter could not be found.", result.Payload["error"]);
        Assert.Equal("failed", translator.ToolCalls[0].Status);
    }

    [Fact]
    public void ignores_a_result_for_a_call_it_never_announced()
    {
        var translator = new LangflowEventTranslator();

        // A pill that was never shown cannot be resolved; emitting the result alone
        // would leave the client with a tool_result it cannot attach to anything.
        Assert.Empty(translator.Accept(Frame("tool_result", """{"callId":"ghost","result":{}}""")));
    }

    [Fact]
    public void passes_an_unmapped_tool_name_through_instead_of_killing_the_turn()
    {
        var translator = new LangflowEventTranslator();

        var call = Single(translator.Accept(
            Frame("tool_call", """{"callId":"c","name":"SomeBrandNewTool","args":{}}""")));

        // Node aborts with `unknown_tool` because there it means the model
        // hallucinated. Here the flow genuinely owns tools we have no name for yet
        // and a sibling is renaming them right now — failing the answer over a label
        // would be the worse outcome.
        Assert.Equal("SomeBrandNewTool", call.Payload["name"]);
        Assert.Equal(false, call.Payload["needsConfirmation"]);
    }

    [Theory]
    [InlineData("SaveTaskTool", "createTask")]
    [InlineData("Save Task Tool", "createTask")]
    [InlineData("save_task_tool", "createTask")]
    [InlineData("UpdateTaskTool", "updateTask")]
    [InlineData("createTask", "createTask")]
    [InlineData("deleteAllTasks", "deleteAllTasks")]
    public void maps_langflow_component_names_onto_contract_tool_names(string langflow, string expected) =>
        Assert.Equal(expected, LangflowToolNames.ToContractName(langflow));

    // ---- sequence and errors ------------------------------------------------

    [Fact]
    public void opens_with_a_sources_frame_and_closes_with_done()
    {
        var translator = new LangflowEventTranslator();

        var start = translator.Start();
        var tail = translator.Complete().ToList();

        // sources → … → done, always. The empty list is honest: the baseline flow
        // reports no grounding citations, and the frame is still sent because the
        // client reads its absence as "sources unchanged".
        Assert.Equal(AiStreamEvents.SourcesKind, start.Kind);
        Assert.Empty((IReadOnlyList<AiStreamSource>)start.Payload["sources"]!);
        Assert.Equal(AiStreamEvents.DoneKind, tail[^1].Kind);
    }

    [Fact]
    public void turns_a_langflow_error_frame_into_an_error_event()
    {
        var translator = new LangflowEventTranslator();

        var error = Single(translator.Accept(Frame("error", """{"error":"Mistral rate limit"}""")));

        Assert.Equal(AiStreamEvents.ErrorKind, error.Kind);
        Assert.Equal("langflow_error", error.Payload["code"]);
        Assert.Equal("Mistral rate limit", error.Payload["message"]);
        Assert.True(translator.Ended);
    }

    [Fact]
    public void still_produces_done_after_an_error_so_the_route_can_settle_the_quota()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(Frame("error", """{"message":"boom"}""")).ToList();

        // The route refunds the reserved slot unless `done` arrives, so a translator
        // that swallowed it would silently charge the user for a failed turn.
        Assert.Equal(AiStreamEvents.DoneKind, translator.Complete().Last().Kind);
    }

    [Fact]
    public void ignores_an_event_name_it_does_not_know()
    {
        var translator = new LangflowEventTranslator();

        Assert.Empty(translator.Accept(Frame("vertices_sorted", """{"ids":["Agent-ieuuD"]}""")));
    }

    // ---- the run-output dig -------------------------------------------------

    [Fact]
    public void digs_the_final_text_out_of_a_full_run_result()
    {
        var end = Parse("""
            {
              "result": {
                "session_id": "u1",
                "outputs": [{
                  "outputs": [{
                    "results": {"message": {"text": "Booked for Tuesday."}}
                  }]
                }]
              }
            }
            """);

        Assert.Equal("Booked for Tuesday.", LangflowRunOutput.FinalText(end));
    }

    [Fact]
    public void returns_null_rather_than_throwing_when_the_run_result_is_shaped_differently()
    {
        // The sibling rewriting the planning agent's output schema will move these
        // paths. Every level is defensive so a rename degrades to "no fallback
        // text", not to a crash mid-answer.
        Assert.Null(LangflowRunOutput.FinalText(Parse("""{"result":{"outputs":[{"outputs":[{}]}]}}""")));
        Assert.Null(LangflowRunOutput.FinalText(Parse("""{"result":{"outputs":"nope"}}""")));
        Assert.Null(LangflowRunOutput.FinalText(Parse("""{}""")));
    }

    // ---- helpers ------------------------------------------------------------

    private static LangflowFrame Frame(string name, string dataJson) => new(name, Parse(dataJson));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string EndWithText(string text) =>
        """{"result":{"outputs":[{"outputs":[{"results":{"message":{"text":__TEXT__}}}]}]}}"""
            .Replace("__TEXT__", JsonSerializer.Serialize(text), StringComparison.Ordinal);

    private static string ToolUse(string id, string name, string? output) =>
        $$"""
        {"content_blocks":[{"contents":[{"type":"tool_use","id":{{JsonSerializer.Serialize(id)}},
        "name":{{JsonSerializer.Serialize(name)}},"tool_input":{}
        {{(output is null ? string.Empty : $",\"output\":{output}")}}}]}]}
        """;

    private static AiStreamEvent Single(IEnumerable<AiStreamEvent> events) => Assert.Single(events.ToList());
}
