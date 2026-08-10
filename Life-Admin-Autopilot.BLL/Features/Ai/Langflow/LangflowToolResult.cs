using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// Digs the tool's own answer out of the wrappers Langflow puts around it.
///
/// <para>
/// <b>Why.</b> The chat renders a created matter as a card by reading
/// <c>result.task</c> (<c>ToolCallCard.tsx</c>). What arrives on the wire is that
/// object behind two layers of JSON-<i>string</i> encoding — Langflow wraps a tool's
/// return in <c>content</c>, and our HTTP tool wraps its own body in <c>value</c>:
/// </para>
///
/// <code>
/// {"content": "{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {…}}\"}",
///  "artifact": {"value": "{\"ok\": true, \"task\": {…}}"},
///  "status": "success", "name": "createTask", …}
/// </code>
///
/// <para>
/// So <c>result.task</c> is <c>undefined</c>, the card silently degrades to the plain
/// ledger row, and the matter the agent just created is invisible. Captured live —
/// the shape above is a real <c>createTask</c> result, not a reconstruction.
/// </para>
///
/// <para>
/// <b>It only ever unwraps.</b> A payload that is already the tool's object is
/// returned untouched, and anything it cannot make sense of is returned as-is rather
/// than dropped: showing a raw result beats showing nothing, and a future Langflow
/// that stops double-wrapping must keep working without a code change.
/// </para>
/// </summary>
public static class LangflowToolResult
{
    /// <summary>Wrappers are shallow in practice; the cap is a runaway guard, not a limit.</summary>
    private const int MaxDepth = 6;

    /// <summary>The keys Langflow and our tools wrap a payload in, outermost first.</summary>
    private static readonly string[] WrapperKeys = { "content", "value", "artifact", "output" };

    /// <summary>
    /// Returns the innermost object the tool actually produced — the one carrying
    /// <c>ok</c>/<c>task</c> — or the input unchanged when there is nothing to unwrap.
    /// </summary>
    public static JsonElement? Unwrap(JsonElement? result)
    {
        if (result is not { } element)
        {
            return null;
        }

        var current = element;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            // A JSON string that is itself JSON: the encoding this exists to undo.
            if (current.ValueKind == JsonValueKind.String)
            {
                var parsed = TryParse(current.GetString());
                if (parsed is null)
                {
                    return current;     // a genuine string result, e.g. a tool that returns text
                }

                current = parsed.Value;
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object)
            {
                return current;
            }

            // Already the tool's own answer — stop before unwrapping past it. `task`
            // is what the card reads; `ok` is the envelope our tools return.
            if (current.TryGetProperty("task", out _) || current.TryGetProperty("ok", out _))
            {
                return current;
            }

            var moved = false;
            foreach (var key in WrapperKeys)
            {
                if (current.TryGetProperty(key, out var inner)
                    && inner.ValueKind is JsonValueKind.String or JsonValueKind.Object)
                {
                    current = inner;
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                return current;
            }
        }

        return current;
    }

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
