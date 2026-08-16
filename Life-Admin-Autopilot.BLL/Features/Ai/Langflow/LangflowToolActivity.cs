using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>The only place on the wire that reports one tool invocation per tool
/// invocation.</b>
///
/// <para>
/// <b>Why this file exists.</b> Tool activity was read out of <c>add_message</c>'s
/// <c>content_blocks[].contents[]</c>, and that list is NOT one entry per call.
/// Langflow keeps <b>one <c>tool_use</c> block per tool NAME</b> and overwrites it in
/// place every time the agent calls that tool again. Measured against live 1.11.2, one
/// turn that created three matters ("add buy milk, buy bread, and buy eggs to my
/// list") produced three real invocations and only ever TWO blocks, and block 0 flipped
/// its <c>tool_input</c> from <c>buy milk</c> to <c>buy bread</c> between two
/// redeliveries of the same message id:
/// </para>
///
/// <code>
/// line 25  b0 createTask title='buy milk'   output=null
///          b1 createTask title='buy eggs'   output=null
/// line 37  b0 createTask title='buy bread'  output=null   ← same block, new call
///          b1 createTask title='buy eggs'   output=null
/// line 81  b0 createTask title='buy bread'  output={… "buy bread" …}
/// </code>
///
/// <para>
/// So the block index is not an invocation identity. A call announced from block 0 was
/// announced with one invocation's arguments and later resolved with a DIFFERENT
/// invocation's output — the client showed a <c>createTask</c> pill reading "buy milk"
/// with "buy bread" attached to it — and the invocation whose block got overwritten
/// was never announced at all. Two frames for three calls, one of them mis-paired.
/// </para>
///
/// <para>
/// <b>What this reads instead.</b> The <c>log</c> frames, which carry the agent's own
/// LangChain callbacks and therefore one record per invocation, each with the real
/// <c>tool_call</c> id the model minted:
/// </para>
///
/// <code>
/// {"event":"log","data":{"component_id":"PlanningAgent-v4","message":{
///    "type":"chain_start","name":"tools",
///    "inputs":[{"name":"createTask","args":{…},"id":"3a5fab9f-…","type":"tool_call"}]}}}
///
/// {"event":"log","data":{"message":{"type":"tool_end","output":{
///    "type":"tool","name":"createTask","tool_call_id":"3a5fab9f-…",
///    "content":"…","artifact":{…},"status":"success"}}}}
/// </code>
///
/// <para>
/// Those two are joined by <c>tool_call_id</c>, so a result can never be attached to
/// the wrong call — which matters because completion order is NOT call order (the same
/// capture called milk, bread, eggs and ended milk, eggs, bread).
/// </para>
///
/// <para>
/// <b>Not the only source, on purpose.</b> A deployment that does not stream <c>log</c>
/// frames still has to work, so <see cref="LangflowEventTranslator"/> keeps reading
/// <c>add_message</c> too and the two are reconciled by
/// <see cref="Fingerprint"/> — the same invocation seen twice must not become two
/// pills. See the translator for that half.
/// </para>
/// </summary>
public static class LangflowToolActivity
{
    /// <summary>The <c>message.type</c> that announces a batch of tool calls.</summary>
    private const string ChainStart = "chain_start";

    /// <summary>The <c>message.type</c> that reports one call's outcome.</summary>
    private const string ToolEnd = "tool_end";

    /// <summary>The <c>type</c> discriminator on an entry of <c>chain_start.inputs</c>.</summary>
    private const string ToolCallType = "tool_call";

    /// <summary>Langflow's word for "the tool did not throw".</summary>
    private const string SuccessStatus = "success";

    /// <summary>Arguments are shallow; the cap is a runaway guard, not a limit.</summary>
    private const int MaxFingerprintDepth = 8;

    /// <summary>One tool invocation, as the agent's own callback reported it.</summary>
    public readonly record struct ToolInvocation(string CallId, string Name, JsonElement? Args);

    /// <summary>One invocation's outcome, keyed by the id the invocation carried.</summary>
    public readonly record struct ToolOutcome(string CallId, JsonElement? Result, string? Error);

    /// <summary>
    /// Every tool call announced by a <c>log</c> frame, or nothing at all for the many
    /// <c>log</c> frames that carry something else (the agent's own
    /// <c>chain_start</c> is a sibling shape whose <c>inputs</c> is an OBJECT of
    /// messages, not an array of calls — hence the array test rather than a name test).
    /// </summary>
    public static IEnumerable<ToolInvocation> Invocations(JsonElement data)
    {
        var message = LangflowWireContract.ReadElement(data, "message");
        if (message is null || LangflowWireContract.ReadString(message.Value, "type") != ChainStart)
        {
            yield break;
        }

        var inputs = LangflowWireContract.ReadElement(message.Value, "inputs");
        if (inputs is not { ValueKind: JsonValueKind.Array })
        {
            yield break;
        }

        foreach (var entry in inputs.Value.EnumerateArray())
        {
            if (LangflowWireContract.ReadString(entry, "type") != ToolCallType)
            {
                continue;
            }

            var name = LangflowWireContract.ReadString(entry, "name");
            var callId = LangflowWireContract.ReadString(entry, "id");

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(callId))
            {
                continue;
            }

            yield return new ToolInvocation(callId, name, LangflowWireContract.ReadElement(entry, "args"));
        }
    }

    /// <summary>
    /// The outcome a <c>log</c> frame reports, or null when it reports none.
    ///
    /// <para>
    /// <b>The success shape is measured; the failure shape is not.</b> Every live
    /// <c>tool_end</c> captured carried <c>status: "success"</c>, so anything else is
    /// treated as a failure and the payload becomes the error text. That is the
    /// conservative reading: a call whose outcome cannot be confirmed must not be
    /// recorded as executed, because <see cref="FabricatedActionGuard"/> trusts that
    /// status.
    /// </para>
    /// </summary>
    public static ToolOutcome? Outcome(JsonElement data)
    {
        var message = LangflowWireContract.ReadElement(data, "message");
        if (message is null || LangflowWireContract.ReadString(message.Value, "type") != ToolEnd)
        {
            return null;
        }

        var output = LangflowWireContract.ReadElement(message.Value, "output");
        if (output is null)
        {
            return null;
        }

        var callId = LangflowWireContract.ReadString(output.Value, "tool_call_id");
        if (string.IsNullOrEmpty(callId))
        {
            return null;
        }

        var status = LangflowWireContract.ReadString(output.Value, "status");
        var failed = status is not null && !string.Equals(status, SuccessStatus, StringComparison.Ordinal);

        var error = failed
            ? LangflowWireContract.ReadFirstString(output.Value, "error", "content") ?? status
            : LangflowWireContract.ReadString(output.Value, "error");

        return new ToolOutcome(callId, output, error);
    }

    /// <summary>
    /// A stable key for "this exact invocation", used to recognise the SAME call
    /// arriving twice — once from a <c>log</c> frame and once from the
    /// <c>add_message</c> row Langflow builds out of it.
    ///
    /// <para>
    /// Name plus canonical arguments, because that is the only thing both shapes carry:
    /// the <c>log</c> frame's <c>args</c> and the <c>tool_use</c> block's
    /// <c>tool_input</c> are the same dictionary, serialized twice. Keys are sorted and
    /// strings re-encoded so two serializations of one dictionary cannot disagree.
    /// </para>
    ///
    /// <para>
    /// <b>It is a memory key and never leaves the process</b> — arguments include the
    /// caller's bearer token. What reaches the client is the call id, which is either
    /// Langflow's own uuid or a per-turn mint.
    /// </para>
    ///
    /// <para>
    /// <b>Two invocations of one tool with byte-identical arguments collapse into
    /// one</b> on the <c>add_message</c>-only path. That is unchanged from the block-index
    /// scheme, which collapsed them too, and the <c>log</c> path separates them properly
    /// because it carries a real id per call.
    /// </para>
    /// </summary>
    public static string Fingerprint(string name, JsonElement? args)
    {
        var builder = new StringBuilder(name);
        builder.Append(' ');
        Canonicalize(args, builder, 0);
        return builder.ToString();
    }

    private static void Canonicalize(JsonElement? value, StringBuilder into, int depth)
    {
        if (value is not { } element || depth >= MaxFingerprintDepth)
        {
            into.Append("null");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                into.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        into.Append(',');
                    }

                    firstProperty = false;
                    into.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Canonicalize(property.Value, into, depth + 1);
                }

                into.Append('}');
                break;

            case JsonValueKind.Array:
                into.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        into.Append(',');
                    }

                    firstItem = false;
                    Canonicalize(item, into, depth + 1);
                }

                into.Append(']');
                break;

            case JsonValueKind.String:
                // Re-encoded rather than copied: `"é"` and `"é"` are the same
                // value and must not fingerprint differently.
                into.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            default:
                into.Append(element.GetRawText());
                break;
        }
    }
}
