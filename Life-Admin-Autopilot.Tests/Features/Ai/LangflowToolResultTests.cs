using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// Unwrapping a tool result to the object the chat's card actually reads.
///
/// <para>
/// The payload in <see cref="Captured"/> is a REAL <c>createTask</c> result taken off
/// the wire, not a reconstruction — including the double JSON-string encoding that
/// made <c>result.task</c> undefined and silently downgraded every created matter
/// from a card to a ledger row.
/// </para>
/// </summary>
public sealed class LangflowToolResultTests
{
    private const string Captured = """
        {"content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"6a7a64ba99254087f169b5cc\\\", \\\"title\\\": \\\"file the VAT return\\\", \\\"domain\\\": \\\"finance\\\", \\\"kind\\\": \\\"reminder\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"dueAt\\\": \\\"2026-09-12T12:00:00.000Z\\\", \\\"tags\\\": []}}\"}",
         "additional_kwargs": {}, "type": "tool", "name": "createTask",
         "artifact": {"value": "{\"ok\": true, \"task\": {\"id\": \"6a7a64ba99254087f169b5cc\"}}"},
         "status": "success"}
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void digs_the_task_out_of_the_real_captured_payload()
    {
        var unwrapped = LangflowToolResult.Unwrap(Parse(Captured));

        Assert.NotNull(unwrapped);
        Assert.True(unwrapped!.Value.TryGetProperty("task", out var task));
        Assert.Equal("file the VAT return", task.GetProperty("title").GetString());
        Assert.Equal("6a7a64ba99254087f169b5cc", task.GetProperty("id").GetString());
        Assert.True(unwrapped.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void leaves_an_already_unwrapped_result_alone()
    {
        // A flow that stops double-wrapping must keep working with no code change.
        var plain = Parse("""{"ok":true,"task":{"id":"abc","title":"Renew insurance"}}""");
        var unwrapped = LangflowToolResult.Unwrap(plain);

        Assert.Equal("Renew insurance", unwrapped!.Value.GetProperty("task").GetProperty("title").GetString());
    }

    [Fact]
    public void stops_at_the_tool_object_rather_than_unwrapping_past_it()
    {
        // `task` itself contains no wrapper keys, but a task could one day have a
        // field called `content`. Stopping at ok/task is what prevents that.
        var unwrapped = LangflowToolResult.Unwrap(Parse(
            """{"content":"{\"ok\":true,\"task\":{\"id\":\"1\",\"content\":\"notes here\"}}"}"""));

        Assert.True(unwrapped!.Value.TryGetProperty("task", out var task));
        Assert.Equal("notes here", task.GetProperty("content").GetString());
    }

    [Fact]
    public void returns_a_plain_string_result_unchanged()
    {
        var unwrapped = LangflowToolResult.Unwrap(Parse("""{"content":"just some text"}"""));

        Assert.Equal(JsonValueKind.String, unwrapped!.Value.ValueKind);
        Assert.Equal("just some text", unwrapped.Value.GetString());
    }

    [Fact]
    public void survives_a_shape_it_does_not_recognise()
    {
        // Never drop a payload: showing something raw beats showing nothing.
        var odd = Parse("""{"unexpected":{"deeply":"nested"}}""");
        Assert.Equal(JsonValueKind.Object, LangflowToolResult.Unwrap(odd)!.Value.ValueKind);
    }

    [Fact]
    public void handles_null()
    {
        Assert.Null(LangflowToolResult.Unwrap(null));
    }

    [Fact]
    public void the_frame_the_client_receives_carries_task_at_the_top_level()
    {
        // End to end through the translator: this is what ToolCallCard will read.
        var translator = new LangflowEventTranslator();
        translator.Accept(new LangflowFrame("tool_call", Parse(
            """{"callId":"c1","name":"createTask","args":{"title":"file the VAT return"}}"""))).ToList();

        var frames = translator.Accept(new LangflowFrame("tool_result", Parse(
            $$"""{"callId":"c1","result":{{Captured}}}"""))).ToList();

        var result = (JsonElement?)Assert.Single(frames).Payload["result"];
        Assert.Equal(
            "file the VAT return",
            result!.Value.GetProperty("task").GetProperty("title").GetString());
    }
}
