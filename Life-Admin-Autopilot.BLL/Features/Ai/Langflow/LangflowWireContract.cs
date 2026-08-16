using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>THE ONE PLACE that knows what Langflow puts on the wire.</b>
///
/// <para>
/// Everything downstream of this file speaks <see cref="AiStreamEvent"/>, the shape
/// the shipped chat UI already parses. When the planning agent's output schema
/// changes — and it is being rewritten right now, in a sibling worktree, under
/// <c>langflow/</c> — the edit lands here and in
/// <see cref="LangflowEventTranslator"/>, and nowhere else. That containment is the
/// whole point of the type; do not let a <c>content_blocks</c> lookup leak into the
/// provider or a route.
/// </para>
///
/// <para><b>Transport.</b> <c>POST {BaseUrl}/api/v1/run/{FlowId}?stream=true</c>
/// answers with a chunked body of newline-delimited frames. Two encodings are seen
/// in the wild and both are accepted by <see cref="TryParseLine"/>:</para>
///
/// <list type="number">
///   <item>
///     <b>NDJSON</b> — one JSON object per line:
///     <c>{"event":"token","data":{"chunk":"Hel"}}</c>. This is what Langflow's
///     <c>/api/v1/run</c> emits.
///   </item>
///   <item>
///     <b>SSE</b> — <c>event: token</c> / <c>data: {...}</c> line pairs, which some
///     deployments front the same stream with. A bare <c>data:</c> line carrying the
///     whole <c>{"event":…,"data":…}</c> object is handled too.
///   </item>
/// </list>
///
/// <para><b>Frames we act on.</b> Anything else is ignored rather than treated as an
/// error — an unrecognised frame from a newer Langflow must not abort a turn:</para>
///
/// <list type="table">
///   <listheader><term>event</term><description>expected <c>data</c></description></listheader>
///   <item>
///     <term><c>token</c></term>
///     <description><c>{"chunk":"…"}</c> (also read: <c>text</c>). One incremental
///     slice of the answer.</description>
///   </item>
///   <item>
///     <term><c>add_message</c></term>
///     <description>A whole message row. Tool activity arrives here, under
///     <c>content_blocks[].contents[]</c> where <c>type == "tool_use"</c>, carrying
///     <c>name</c>, <c>tool_input</c> and either <c>output</c> or <c>error</c>. Text is
///     under <c>text</c> and is used ONLY as a fallback when no <c>token</c> frames
///     arrived, so a streaming flow does not double-print its answer.</description>
///   </item>
///   <item>
///     <term><c>tool_call</c> / <c>tool_result</c></term>
///     <description>The explicit forms, if the flow emits them directly:
///     <c>{"callId","name","args"}</c> and <c>{"callId","result","error"}</c>. Preferred
///     when present because they carry a stable id.</description>
///   </item>
///   <item>
///     <term><c>log</c></term>
///     <description>The agent's LangChain callbacks, and the ONLY frame that reports
///     one record per tool invocation: <c>message.type == "chain_start"</c> with an
///     <c>inputs</c> ARRAY of <c>{name, args, id, type:"tool_call"}</c>, and
///     <c>message.type == "tool_end"</c> with <c>output.tool_call_id</c>. See
///     <see cref="LangflowToolActivity"/> for why <c>add_message</c> cannot be trusted
///     to enumerate calls on its own.</description>
///   </item>
///   <item>
///     <term><c>end</c></term>
///     <description><c>{"result":{"outputs":[…]}}</c>. Ends the turn. The final text
///     is dug out of
///     <c>result.outputs[0].outputs[0].results.message.text</c> and used only as the
///     same fallback.</description>
///   </item>
///   <item>
///     <term><c>error</c></term>
///     <description><c>{"error":"…"}</c> or <c>{"message":"…"}</c>. Becomes an error
///     frame inside the 200, because by the time it arrives the headers have long
///     flushed.</description>
///   </item>
/// </list>
///
/// <para>
/// <b>Not verified end to end.</b> No Langflow instance was reachable while this was
/// written (nothing on :7860), so the shapes above are authored from Langflow's
/// documented streaming surface and from
/// <c>langflow/planning-agent.v3.baseline.json</c> — a 7-node flow whose Agent is
/// wired to <c>SaveTaskTool</c>, <c>UpdateTaskTool</c> and a Python interpreter. The
/// translator is exercised against recorded/synthetic frames only.
/// </para>
/// </summary>
public static class LangflowWireContract
{
    public const string TokenEvent = "token";
    public const string AddMessageEvent = "add_message";
    public const string ToolCallEvent = "tool_call";
    public const string ToolResultEvent = "tool_result";
    public const string LogEvent = "log";
    public const string EndEvent = "end";
    public const string ErrorEvent = "error";

    /// <summary>The SSE field prefixes, for the encoding that uses them.</summary>
    private const string SseEventPrefix = "event:";
    private const string SseDataPrefix = "data:";

    /// <summary>
    /// Parse one physical line into a frame. Returns false for blank lines, SSE
    /// comments (<c>:</c> keep-alives), and anything that is not JSON — none of which
    /// is an error.
    ///
    /// <para>
    /// <paramref name="pendingEventName"/> carries the <c>event:</c> line of an SSE
    /// pair across to its <c>data:</c> line. The caller owns it; pass the same
    /// variable back in on every line of one stream.
    /// </para>
    /// </summary>
    public static bool TryParseLine(string line, ref string? pendingEventName, out LangflowFrame frame)
    {
        frame = default;

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith(SseEventPrefix, StringComparison.Ordinal))
        {
            pendingEventName = trimmed[SseEventPrefix.Length..].Trim();
            return false;
        }

        // An SSE comment. Langflow's own keep-alives, not ours.
        if (trimmed[0] == ':')
        {
            return false;
        }

        if (trimmed.StartsWith(SseDataPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[SseDataPrefix.Length..].Trim();
            if (trimmed.Length == 0 || trimmed == "[DONE]")
            {
                return false;
            }
        }

        if (trimmed[0] != '{')
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            // A truncated or non-JSON line is dropped, not fatal. Aborting a whole
            // answer because one keep-alive was malformed is the worse failure.
            return false;
        }

        var root = document.RootElement.Clone();
        document.Dispose();

        var name = root.TryGetProperty("event", out var eventName) && eventName.ValueKind == JsonValueKind.String
            ? eventName.GetString()!
            : pendingEventName;

        // A `data:`-only line whose object IS the payload (no envelope) keeps the
        // event name from the preceding `event:` line.
        var data = root.TryGetProperty("data", out var payload) ? payload : root;

        pendingEventName = null;

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        frame = new LangflowFrame(name, data);
        return true;
    }

    /// <summary>Read a string member, tolerating an absent key or a non-string value.</summary>
    public static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>First non-null of several candidate member names.</summary>
    public static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Read an object/array member as an opaque payload, or null.</summary>
    public static JsonElement? ReadElement(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? value
            : null;
}

/// <summary>
/// One parsed Langflow frame: the event name, and its payload as an opaque
/// <see cref="JsonElement"/>. Deliberately NOT a typed DTO per event — the shape is
/// in flux, and a strongly-typed binder would turn every upstream rename into a
/// deserialization crash mid-answer rather than an ignored frame.
/// </summary>
public readonly record struct LangflowFrame(string EventName, JsonElement Data);
