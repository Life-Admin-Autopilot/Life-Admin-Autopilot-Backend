using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// The two nested digs into Langflow's payloads, kept out of
/// <see cref="LangflowEventTranslator"/> so the translation logic stays readable and
/// the path literals sit next to the shape they walk.
///
/// <para>Both are defensive at every level: a missing or differently-typed member
/// yields nothing rather than throwing, because a turn must survive an upstream
/// rename.</para>
/// </summary>
public static class LangflowRunOutput
{
    /// <summary>
    /// Every <c>tool_use</c> content inside a message's content blocks.
    ///
    /// <para>Path: <c>content_blocks[] → contents[] → {type:"tool_use", …}</c>. The
    /// snake_case member names are Langflow's; it does not camelCase its API.</para>
    /// </summary>
    public static IEnumerable<JsonElement> ToolUseContents(JsonElement message)
    {
        var blocks = LangflowWireContract.ReadElement(message, "content_blocks")
                     ?? LangflowWireContract.ReadElement(message, "contentBlocks");

        if (blocks is not { ValueKind: JsonValueKind.Array })
        {
            yield break;
        }

        foreach (var block in blocks.Value.EnumerateArray())
        {
            var contents = LangflowWireContract.ReadElement(block, "contents");
            if (contents is not { ValueKind: JsonValueKind.Array })
            {
                continue;
            }

            foreach (var content in contents.Value.EnumerateArray())
            {
                if (LangflowWireContract.ReadString(content, "type") == "tool_use")
                {
                    yield return content;
                }
            }
        }
    }

    /// <summary>
    /// The final answer inside an <c>end</c> frame.
    ///
    /// <para>Path:
    /// <c>result.outputs[].outputs[].results.message.text</c>, with
    /// <c>…results.message.data.text</c> and a bare <c>…outputs[].messages[].message</c>
    /// as the two shapes Langflow also produces depending on the output component.
    /// The LAST non-empty match wins, matching "the final output component's text".</para>
    /// </summary>
    public static string? FinalText(JsonElement end)
    {
        var result = LangflowWireContract.ReadElement(end, "result") ?? end;
        var outputs = LangflowWireContract.ReadElement(result, "outputs");

        if (outputs is not { ValueKind: JsonValueKind.Array })
        {
            return LangflowWireContract.ReadFirstString(result, "text", "message");
        }

        string? found = null;

        foreach (var run in outputs.Value.EnumerateArray())
        {
            var inner = LangflowWireContract.ReadElement(run, "outputs");
            if (inner is not { ValueKind: JsonValueKind.Array })
            {
                continue;
            }

            foreach (var output in inner.Value.EnumerateArray())
            {
                var text = TextOf(output);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    found = text;
                }
            }
        }

        return found;
    }

    private static string? TextOf(JsonElement output)
    {
        var results = LangflowWireContract.ReadElement(output, "results");
        var message = results is null ? null : LangflowWireContract.ReadElement(results.Value, "message");

        if (message is not null)
        {
            var direct = LangflowWireContract.ReadFirstString(message.Value, "text");
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var data = LangflowWireContract.ReadElement(message.Value, "data");
            if (data is not null)
            {
                var nested = LangflowWireContract.ReadFirstString(data.Value, "text");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        var messages = LangflowWireContract.ReadElement(output, "messages");
        if (messages is { ValueKind: JsonValueKind.Array })
        {
            foreach (var entry in messages.Value.EnumerateArray())
            {
                var text = LangflowWireContract.ReadFirstString(entry, "message", "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }
}
