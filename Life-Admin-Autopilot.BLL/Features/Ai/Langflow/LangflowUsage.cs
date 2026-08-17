using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>Token counts for one agent turn, as Langflow reports them.</summary>
public readonly record struct LangflowTokenUsage(int InputTokens, int OutputTokens, int TotalTokens)
{
    public bool HasCounts => InputTokens > 0 || OutputTokens > 0 || TotalTokens > 0;
}

/// <summary>
/// Pulls token usage out of the Langflow stream.
///
/// <para>
/// <b>Measured against a live 1.11.2 run, not inferred.</b> A real turn against the
/// planning flow was captured and every frame inspected. Usage appears in exactly
/// two places, both carrying identical values:
/// </para>
///
/// <list type="number">
///   <item>
///     <b><c>end</c></b> — at
///     <c>data.result.outputs[0].outputs[0].results.message.properties.usage</c>,
///     and mirrored under <c>…message.data.properties.usage</c>.
///   </item>
///   <item>
///     <b><c>add_message</c></b> — at <c>data.data.properties.usage</c>, but only on
///     rows whose <c>properties.state</c> is <c>complete</c>. Langflow redelivers the
///     same completed row several times (three, in the captured run), so a caller
///     reading these must keep the LAST value rather than summing them.
///   </item>
/// </list>
///
/// <para>
/// <b>The <c>end</c> frame is the one to trust.</b> It arrives exactly once per turn,
/// which removes the redelivery problem entirely. <c>add_message</c> is read only as
/// a fallback, for a flow shape whose final frame carries no outputs.
/// </para>
///
/// <para>
/// <b>Langflow does not report which model produced the tokens.</b> The usage block
/// is counts and nothing else — no model id anywhere on the frame — so pricing a
/// chat turn depends on <c>Ai:Pricing:DefaultChatModel</c> naming whatever the flow's
/// Agent node is wired to. There is no way to derive it from the wire.
/// </para>
/// </summary>
public static class LangflowUsage
{
    private const string UsageKey = "usage";
    private const string InputKey = "input_tokens";
    private const string OutputKey = "output_tokens";
    private const string TotalKey = "total_tokens";

    /// <summary>
    /// Read usage off one frame, if it carries any. False for every frame that does
    /// not — which is most of them — so this is safe to call on the whole stream.
    /// </summary>
    public static bool TryRead(in LangflowFrame frame, out LangflowTokenUsage usage)
    {
        usage = default;

        return frame.EventName switch
        {
            LangflowWireContract.EndEvent => TryReadFromEnd(frame.Data, out usage),
            LangflowWireContract.AddMessageEvent => TryReadFromAddMessage(frame.Data, out usage),
            _ => false,
        };
    }

    /// <summary><c>result.outputs[0].outputs[0].results.message</c>, then the usage block.</summary>
    private static bool TryReadFromEnd(JsonElement data, out LangflowTokenUsage usage)
    {
        usage = default;

        if (!TryGet(data, "result", out var result)
            || !TryFirstOfArray(result, "outputs", out var outer)
            || !TryFirstOfArray(outer, "outputs", out var inner)
            || !TryGet(inner, "results", out var results)
            || !TryGet(results, "message", out var message))
        {
            return false;
        }

        // The mirrored `message.data.properties.usage` is read second so a future
        // Langflow that populates only one of the two still works.
        return TryReadProperties(message, out usage)
            || (TryGet(message, "data", out var nested) && TryReadProperties(nested, out usage));
    }

    /// <summary>
    /// <c>data.properties.usage</c>, gated on <c>state == "complete"</c>.
    ///
    /// <para>
    /// The state gate is what stops a half-filled row being read as a finished turn.
    /// Langflow emits the message row as soon as it exists and rewrites it as the
    /// agent works; only the completed rewrite carries real counts.
    /// </para>
    /// </summary>
    private static bool TryReadFromAddMessage(JsonElement data, out LangflowTokenUsage usage)
    {
        usage = default;

        if (!TryGet(data, "data", out var row))
        {
            // Some encodings put the row at the top level of `data` already.
            row = data;
        }

        if (!TryGet(row, "properties", out var properties))
        {
            return false;
        }

        if (LangflowWireContract.ReadString(properties, "state") is not "complete")
        {
            return false;
        }

        return TryReadUsageBlock(properties, out usage);
    }

    private static bool TryReadProperties(JsonElement container, out LangflowTokenUsage usage)
    {
        usage = default;
        return TryGet(container, "properties", out var properties) && TryReadUsageBlock(properties, out usage);
    }

    private static bool TryReadUsageBlock(JsonElement properties, out LangflowTokenUsage usage)
    {
        usage = default;

        if (!TryGet(properties, UsageKey, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var input = ReadInt(block, InputKey);
        var output = ReadInt(block, OutputKey);

        // Prefer the reported total. It is what the vendor billed, and deriving it
        // would quietly drop any component Langflow counts separately (cached or
        // reasoning tokens, which price differently and are not always in the split).
        var total = ReadInt(block, TotalKey);
        if (total == 0)
        {
            total = input + output;
        }

        usage = new LangflowTokenUsage(input, output, total);
        return usage.HasCounts;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryFirstOfArray(JsonElement element, string name, out JsonElement first)
    {
        first = default;

        if (!TryGet(element, name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            first = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Zero for anything that is not a readable number. A provider reporting a string
    /// or a null where a count belongs should cost us a data point, not a turn.
    /// </summary>
    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? Math.Max(0, parsed)
            : 0;
}
