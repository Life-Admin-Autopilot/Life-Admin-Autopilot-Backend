namespace Life_Admin_Autopilot.Tests.Features.Ai;

using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>One real hold, captured after the token left the model's hands.</b>
///
/// <para>
/// Taken off a live Langflow 1.11.2 answering <i>"Remind me on Monday to go to the mom
/// friend."</i> on 2026-08-17, through the <c>PlanningInput-v4</c> tweak binding, against
/// a throwaway account — the same sentence that produced the phantom-questions incident,
/// replayed against the fixed flow.
/// </para>
///
/// <para>
/// <b>Why this sits beside <see cref="ThreeMatterTurn"/>.</b> That capture pins the
/// pairing rule for three calls to ONE tool. This one pins the rule against the argument
/// shape that was suspected of breaking it: <c>secondary_question</c> and
/// <c>secondary_kind</c>, added when a hold learned to raise two questions. If
/// <c>chain_start</c> and the <c>add_message</c> block serialized the new keys
/// differently, the fingerprint fallback would stop matching and the result would be
/// handed to the wrong call — the mechanism behind the incident's misattributed facts.
/// They do not: the key sets are identical and both carry the new arguments.
/// </para>
///
/// <para>
/// <b>It also pins the security property.</b> No frame here contains a bearer token,
/// because <c>access_token</c> is no longer an argument the model supplies. That is a
/// property of the capture, not of a redaction pass — nothing was removed from these
/// frames. Ids are shortened to <c>id-n</c> and Mongo ids to <c>task-n</c>; nothing else
/// was touched.
/// </para>
/// </summary>
internal static class PhantomHoldTurn
{
    /// <summary>The captured frames, in wire order.</summary>
    public static IReadOnlyList<LangflowFrame> Frames => _frames ??= Parse(Lines);

    private static IReadOnlyList<LangflowFrame>? _frames;

    /// <summary>log / chain_start — the ONLY frame carrying the tool_call id. Note the args: `secondary_question` and `secondary_kind` are present, and `access_token` is NOT.</summary>
    private const string ChainStart =
        """
        {"event": "log", "data": {"name": "Chain Start", "message": {"type": "chain_start", "serialized": null, "inputs": [{"name": "holdForClarification", "args": {"cost_of_wrong": "low", "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"1:00 PM\",\"dueAt\":\"2026-08-17T13:00:00+03:00\"},{\"label\":\"6:00 PM\",\"dueAt\":\"2026-08-17T18:00:00+03:00\"}]", "question_kind": "date", "secondary_question": "Which friend is it?", "secondary_kind": "detail", "domain": "family", "title": "Go to the mom friend", "priority": "normal", "question": "When should I remind you?", "source_text": "Remind me on Monday to go to the mom friend."}, "id": "id-1", "type": "tool_call"}], "run_id": "id-2", "parent_run_id": "id-3", "tags": ["graph:step:4"], "metadata": {"ls_integration": "langchain_create_agent", "langgraph_step": 4, "langgraph_node": "tools", "langgraph_triggers": ["__pregel_push"], "langgraph_path": ["__pregel_push", 0, false], "langgraph_checkpoint_ns": "tools:id-4"}, "name": "tools"}, "type": "object", "output": "response", "component_id": "PlanningAgent-v4"}}
        """;

    /// <summary>add_message — the same call as a content block. It has NO id of any kind, which is why the translator has to fingerprint `tool_input` to know which call it belongs to.</summary>
    private const string AddMessage =
        """
        {"event": "add_message", "data": {"text_key": "text", "data": {"timestamp": "2026-08-17 03:55:38.333771 UTC", "sender": "Machine", "sender_name": "AI", "session_id": "phantom-capture-1", "context_id": "", "text": "", "files": [], "error": false, "edit": false, "properties": {"text_color": null, "background_color": null, "edited": false, "source": {"id": null, "display_name": null, "source": null}, "icon": "Bot", "allow_markdown": false, "positive_feedback": null, "state": "partial", "targets": [], "usage": null, "build_duration": null}, "category": "message", "content_blocks": [], "session_metadata": {"graph_run_id": "id-1"}, "id": "id-2", "flow_id": "id-3", "run_id": "id-1", "duration": null}, "default_value": "", "sender": "Machine", "sender_name": "AI", "files": [], "session_id": "phantom-capture-1", "context_id": "", "run_id": "id-1", "timestamp": "2026-08-17 03:55:38.333771 UTC", "flow_id": "id-3", "error": false, "edit": false, "properties": {"text_color": null, "background_color": null, "edited": false, "source": {"id": null, "display_name": null, "source": null}, "icon": "Bot", "allow_markdown": false, "positive_feedback": null, "state": "partial", "targets": [], "usage": null, "build_duration": null}, "category": "message", "content_blocks": [{"title": "Agent Steps", "contents": [{"type": "tool_use", "duration": 10, "header": {"title": "Accessing **holdForClarification**", "icon": "Hammer"}, "name": "holdForClarification", "tool_input": {"cost_of_wrong": "low", "options": "[{\"label\":\"9:00 AM\",\"dueAt\":\"2026-08-17T09:00:00+03:00\"},{\"label\":\"1:00 PM\",\"dueAt\":\"2026-08-17T13:00:00+03:00\"},{\"label\":\"6:00 PM\",\"dueAt\":\"2026-08-17T18:00:00+03:00\"}]", "question_kind": "date", "secondary_question": "Which friend is it?", "secondary_kind": "detail", "domain": "family", "title": "Go to the mom friend", "priority": "normal", "question": "When should I remind you?", "source_text": "Remind me on Monday to go to the mom friend."}, "output": null, "error": null}], "allow_markdown": true, "media_url": null}], "duration": null, "session_metadata": {"graph_run_id": "id-1"}, "text": "", "id": "id-2"}}
        """;

    /// <summary>log / tool_end — the outcome. Also unidentified; it pairs by arrival against the open call.</summary>
    private const string ToolEnd =
        """
        {"event": "log", "data": {"name": "Tool End", "message": {"type": "tool_end", "output": {"content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"task-1\\\", \\\"title\\\": \\\"Go to the mom friend\\\", \\\"domain\\\": \\\"family\\\", \\\"kind\\\": \\\"reminder\\\", \\\"status\\\": \\\"open\\\", \\\"priority\\\": \\\"normal\\\", \\\"dueAt\\\": \\\"2026-08-17T06:00:00.000Z\\\", \\\"tags\\\": []}, \\\"clarification\\\": {\\\"id\\\": \\\"task-2\\\", \\\"taskId\\\": \\\"task-1\\\", \\\"question\\\": \\\"When should I remind you?\\\", \\\"kind\\\": \\\"date\\\", \\\"costOfWrong\\\": \\\"low\\\", \\\"options\\\": [{\\\"label\\\": \\\"9:00 AM\\\", \\\"dueAt\\\": \\\"2026-08-17T06:00:00.000Z\\\"}, {\\\"label\\\": \\\"1:00 PM\\\", \\\"dueAt\\\": \\\"2026-08-17T10:00:00.000Z\\\"}, {\\\"label\\\": \\\"6:00 PM\\\", \\\"dueAt\\\": \\\"2026-08-17T15:00:00.000Z\\\"}]}, \\\"clarifications\\\": [{\\\"id\\\": \\\"task-2\\\", \\\"taskId\\\": \\\"task-1\\\", \\\"question\\\": \\\"When should I remind you?\\\", \\\"kind\\\": \\\"date\\\", \\\"costOfWrong\\\": \\\"low\\\", \\\"options\\\": [{\\\"label\\\": \\\"9:00 AM\\\", \\\"dueAt\\\": \\\"2026-08-17T06:00:00.000Z\\\"}, {\\\"label\\\": \\\"1:00 PM\\\", \\\"dueAt\\\": \\\"2026-08-17T10:00:00.000Z\\\"}, {\\\"label\\\": \\\"6:00 PM\\\", \\\"dueAt\\\": \\\"2026-08-17T15:00:00.000Z\\\"}]}, {\\\"id\\\": \\\"task-3\\\", \\\"taskId\\\": \\\"task-1\\\", \\\"question\\\": \\\"Which friend is it?\\\", \\\"kind\\\": \\\"detail\\\", \\\"costOfWrong\\\": \\\"low\\\", \\\"options\\\": []}], \\\"clarificationId\\\": \\\"task-2\\\"}\"}", "additional_kwargs": {}, "response_metadata": {}, "type": "tool", "name": "holdForClarification", "id": null, "tool_call_id": "id-1", "artifact": {"value": "{\"ok\": true, \"task\": {\"id\": \"task-1\", \"title\": \"Go to the mom friend\", \"domain\": \"family\", \"kind\": \"reminder\", \"status\": \"open\", \"priority\": \"normal\", \"dueAt\": \"2026-08-17T06:00:00.000Z\", \"tags\": []}, \"clarification\": {\"id\": \"task-2\", \"taskId\": \"task-1\", \"question\": \"When should I remind you?\", \"kind\": \"date\", \"costOfWrong\": \"low\", \"options\": [{\"label\": \"9:00 AM\", \"dueAt\": \"2026-08-17T06:00:00.000Z\"}, {\"label\": \"1:00 PM\", \"dueAt\": \"2026-08-17T10:00:00.000Z\"}, {\"label\": \"6:00 PM\", \"dueAt\": \"2026-08-17T15:00:00.000Z\"}]}, \"clarifications\": [{\"id\": \"task-2\", \"taskId\": \"task-1\", \"question\": \"When should I remind you?\", \"kind\": \"date\", \"costOfWrong\": \"low\", \"options\": [{\"label\": \"9:00 AM\", \"dueAt\": \"2026-08-17T06:00:00.000Z\"}, {\"label\": \"1:00 PM\", \"dueAt\": \"2026-08-17T10:00:00.000Z\"}, {\"label\": \"6:00 PM\", \"dueAt\": \"2026-08-17T15:00:00.000Z\"}]}, {\"id\": \"task-3\", \"taskId\": \"task-1\", \"question\": \"Which friend is it?\", \"kind\": \"detail\", \"costOfWrong\": \"low\", \"options\": []}], \"clarificationId\": \"task-2\"}"}, "status": "success"}, "run_id": "id-2", "parent_run_id": "id-3", "tags": ["seq:step:1", "holdForClarification"], "color": "green", "name": "holdForClarification"}, "type": "object", "output": "response", "component_id": "PlanningAgent-v4"}}
        """;

    private static readonly string[] Lines = [ChainStart, AddMessage, ToolEnd];

    private static IReadOnlyList<LangflowFrame> Parse(IEnumerable<string> lines)
    {
        var frames = new List<LangflowFrame>();

        foreach (var line in lines)
        {
            string? pending = null;
            if (LangflowWireContract.TryParseLine(line.ReplaceLineEndings(" "), ref pending, out var frame))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }
}
