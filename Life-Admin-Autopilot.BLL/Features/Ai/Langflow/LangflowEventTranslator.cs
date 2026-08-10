using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// One turn's worth of Langflow frames, translated into the seven-event contract
/// the shipped chat UI parses — <b>frame by frame, as they arrive</b>.
///
/// <para>
/// This type is pure: no HTTP, no clock, no database. Feed it
/// <see cref="LangflowFrame"/>s and it yields <see cref="AiStreamEvent"/>s. That is
/// what makes the translation testable without a Langflow instance, and it is the
/// only file besides <see cref="LangflowWireContract"/> that needs editing when the
/// planning agent's output schema moves.
/// </para>
///
/// <para><b>Sequence it guarantees</b>, regardless of what Langflow sends:
/// <c>sources → (tool_call → tool_result)* → token* → done</c>. <c>quota</c> is the
/// route's job, never this one's.</para>
///
/// <para>
/// <b>Tokens stream live.</b> Node's Gemini loop buffers each round's text and emits
/// one <c>token</c> at the end, because that orchestrator produced a premature
/// narration before its tools ran and then contradicted it. Langflow's Agent streams
/// only the final answer, so the buffer is unnecessary here and dropping it is the
/// whole reason to use the streaming endpoint. The client appends tokens either way.
/// A <c>text</c> carried on <c>add_message</c> or <c>end</c> is used ONLY when no
/// token ever arrived, so a streaming flow never double-prints its answer.
/// </para>
/// </summary>
public sealed class LangflowEventTranslator
{
    private readonly List<TranslatedToolCall> _toolCalls = new();
    private readonly HashSet<string> _announcedCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvedCalls = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _streamed = new();

    /// <summary>
    /// Unwraps the planning agent's JSON envelope so the chat renders the reply
    /// rather than the document carrying it. Passes prose through untouched.
    /// </summary>
    private readonly PlanningEnvelopeReader _envelope = new();

    /// <summary>
    /// Identifies THIS turn, for call ids when Langflow supplies none. One per
    /// translator, and a translator is one turn — see <see cref="CallIdFor"/>.
    /// </summary>
    private readonly string _turnId = Guid.NewGuid().ToString();

    private string? _fallbackText;

    /// <summary>Everything the model actually said, for the persisted assistant turn.</summary>
    public string AssistantText =>
        _streamed.Length > 0 ? _streamed.ToString() : _fallbackText ?? string.Empty;

    /// <summary>Every tool call seen this turn, with its final status.</summary>
    public IReadOnlyList<TranslatedToolCall> ToolCalls => _toolCalls;

    /// <summary>True once an <c>end</c> frame arrived. A stream that stops without one still completes.</summary>
    public bool Ended { get; private set; }

    /// <summary>
    /// The opening frame. Emitted before any Langflow output so the client can clear
    /// the previous turn's citation chips at the same moment it clears its text.
    ///
    /// <para>
    /// <b>Always empty today.</b> Grounding citations come from Node's
    /// <c>contextBuilder</c>, which has no counterpart in the baseline flow — the
    /// Langflow agent retrieves its own context inside the flow and reports no source
    /// list. An empty list is the honest answer; the frame is not skipped because its
    /// absence is what the client reads as "sources unchanged".
    /// </para>
    /// </summary>
    public AiStreamEvent Start() => AiStreamEvents.Sources(Array.Empty<AiStreamSource>());

    /// <summary>Translate one frame. May yield zero, one or two events.</summary>
    public IEnumerable<AiStreamEvent> Accept(LangflowFrame frame)
    {
        switch (frame.EventName)
        {
            case LangflowWireContract.TokenEvent:
                return AcceptToken(frame.Data);

            case LangflowWireContract.ToolCallEvent:
                return AcceptExplicitToolCall(frame.Data);

            case LangflowWireContract.ToolResultEvent:
                return AcceptExplicitToolResult(frame.Data);

            case LangflowWireContract.AddMessageEvent:
                return AcceptAddMessage(frame.Data);

            case LangflowWireContract.EndEvent:
                Ended = true;
                RememberFallback(LangflowRunOutput.FinalText(frame.Data));
                return Array.Empty<AiStreamEvent>();

            case LangflowWireContract.ErrorEvent:
                Ended = true;
                return new[]
                {
                    AiStreamEvents.Error(
                        "langflow_error",
                        LangflowWireContract.ReadFirstString(frame.Data, "error", "message", "detail")
                        ?? "Langflow reported an error."),
                };

            default:
                // Unrecognised frames from a newer Langflow are ignored, never fatal.
                return Array.Empty<AiStreamEvent>();
        }
    }

    /// <summary>
    /// The tail of the turn: the fallback answer if nothing streamed, then
    /// <c>done</c>. Always call it, including after an aborted stream — the route
    /// depends on <c>done</c> to decide whether to refund the quota slot.
    /// </summary>
    public IEnumerable<AiStreamEvent> Complete()
    {
        if (_streamed.Length == 0 && !string.IsNullOrEmpty(_fallbackText))
        {
            yield return AiStreamEvents.Token(_fallbackText);
        }

        yield return AiStreamEvents.Done();
    }

    // ---- token --------------------------------------------------------------

    private IEnumerable<AiStreamEvent> AcceptToken(JsonElement data)
    {
        var chunk = LangflowWireContract.ReadFirstString(data, "chunk", "text", "token");
        if (string.IsNullOrEmpty(chunk))
        {
            return Array.Empty<AiStreamEvent>();
        }

        // The agent speaks a JSON envelope; the chat prints tokens verbatim. Emit
        // only what is inside `reply`, and emit it as it decodes so the answer
        // still streams. Most chunks are envelope scaffolding and yield nothing.
        var spoken = _envelope.Push(chunk);
        if (spoken.Length == 0)
        {
            return Array.Empty<AiStreamEvent>();
        }

        _streamed.Append(spoken);
        return new[] { AiStreamEvents.Token(spoken) };
    }

    // ---- tool activity ------------------------------------------------------

    private IEnumerable<AiStreamEvent> AcceptExplicitToolCall(JsonElement data)
    {
        var name = LangflowWireContract.ReadFirstString(data, "name", "tool", "tool_name");
        if (string.IsNullOrEmpty(name))
        {
            return Array.Empty<AiStreamEvent>();
        }

        var callId = LangflowWireContract.ReadFirstString(data, "callId", "call_id", "id")
                     ?? Guid.NewGuid().ToString();
        var args = LangflowWireContract.ReadElement(data, "args")
                   ?? LangflowWireContract.ReadElement(data, "tool_input")
                   ?? LangflowWireContract.ReadElement(data, "input");

        return Announce(callId, name, args).ToArray();
    }

    private IEnumerable<AiStreamEvent> AcceptExplicitToolResult(JsonElement data)
    {
        var callId = LangflowWireContract.ReadFirstString(data, "callId", "call_id", "id");
        if (string.IsNullOrEmpty(callId))
        {
            return Array.Empty<AiStreamEvent>();
        }

        var error = LangflowWireContract.ReadFirstString(data, "error");
        var result = LangflowWireContract.ReadElement(data, "result")
                     ?? LangflowWireContract.ReadElement(data, "output");

        return Resolve(callId, result, error).ToArray();
    }

    /// <summary>
    /// Langflow's own shape: a whole message row whose
    /// <c>content_blocks[].contents[]</c> entries of type <c>tool_use</c> describe
    /// what the agent did. The same message is re-sent as it fills in, so both the
    /// announcement and the resolution are de-duplicated by call id.
    /// </summary>
    private IEnumerable<AiStreamEvent> AcceptAddMessage(JsonElement data)
    {
        var events = new List<AiStreamEvent>();

        // The MESSAGE's id, which is stable across redeliveries — see CallIdFor.
        var messageId = LangflowWireContract.ReadFirstString(data, "id", "message_id");
        var index = -1;

        foreach (var content in LangflowRunOutput.ToolUseContents(data))
        {
            index++;

            var name = LangflowWireContract.ReadFirstString(content, "name", "tool_name", "tool");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var callId = LangflowWireContract.ReadFirstString(content, "id", "tool_call_id", "call_id")
                         ?? CallIdFor(messageId, index);
            var args = LangflowWireContract.ReadElement(content, "tool_input")
                       ?? LangflowWireContract.ReadElement(content, "input")
                       ?? LangflowWireContract.ReadElement(content, "args");

            events.AddRange(Announce(callId, name, args));

            var error = LangflowWireContract.ReadFirstString(content, "error");
            var output = LangflowWireContract.ReadElement(content, "output");

            // Gating lives inside Resolve, not here — it is a property of the call,
            // so it must hold for the explicit tool_result frame too, not only for
            // the output Langflow folds into this row.
            if (output is not null || !string.IsNullOrEmpty(error))
            {
                events.AddRange(Resolve(callId, output, error));
            }
        }

        // Text on a message row is the fallback answer only — see the class remarks.
        RememberFallback(LangflowWireContract.ReadFirstString(data, "text", "message"));

        return events;
    }

    /// <summary>
    /// A call id for a <c>tool_use</c> block that has none of its own.
    ///
    /// <para>
    /// <b>Langflow's tool_use blocks carry NO id.</b> Their keys are exactly
    /// <c>duration, error, header, name, output, tool_input, type</c> — measured off
    /// the live 1.11.2 stream. The enclosing <c>add_message</c> row DOES carry a
    /// stable id, and Langflow redelivers that same row repeatedly as it fills in, so
    /// <c>{messageId}#{index}</c> is the same string on every redelivery and the
    /// dedup holds.
    /// </para>
    ///
    /// <para>
    /// <b>Two bugs came from getting this wrong</b>, and a counter over already-seen
    /// calls caused both. It changed on every redelivery, so it defeated the very
    /// dedup that depended on it: one <c>queryTasks</c> produced 7 <c>tool_call</c>
    /// frames, and one bulk delete produced 7 confirmation cards. It also restarted
    /// at 0 each turn, so a user's SECOND ever bulk delete minted a <c>#0</c> that
    /// collided with the first turn's resolved record — and its confirm button 404'd
    /// permanently. Both measured on the live stack.
    /// </para>
    ///
    /// <para>
    /// The per-turn fallback keeps both properties when a message has no id either:
    /// stable within the turn because the index is positional, unique across turns
    /// because the prefix is a fresh GUID. Cross-turn uniqueness is what the confirm
    /// route depends on — a pending call id must never repeat.
    /// </para>
    /// </summary>
    private string CallIdFor(string? messageId, int index) =>
        $"{messageId ?? _turnId}#{index}";

    private IEnumerable<AiStreamEvent> Announce(string callId, string langflowName, JsonElement? args)
    {
        if (!_announcedCalls.Add(callId))
        {
            yield break;
        }

        var name = LangflowToolNames.ToContractName(langflowName);
        var needsConfirmation = AiToolCatalog.RequiresConfirmation(name);

        _toolCalls.Add(new TranslatedToolCall(
            callId,
            name,
            args,
            needsConfirmation ? "pending_confirmation" : "executed"));

        yield return AiStreamEvents.ToolCall(callId, name, args, needsConfirmation);
    }

    /// <summary>
    /// Settle a call the stream reported an outcome for.
    ///
    /// <para>
    /// <b>A confirmation-gated call is NEVER resolved from the stream.</b> Langflow
    /// runs the tool's dry run and redelivers the row with <c>output</c> populated
    /// inside the same turn — but that payload is a preview, and it says so:
    /// <c>"executed": false, "requiresConfirmation": …</c>. Treating it as an outcome
    /// flipped the stored record from <c>pending_confirmation</c> to <c>executed</c>,
    /// and <c>RequirePendingToolCallAsync</c> then 404'd every single confirmation.
    /// The user got a card whose button could never work.
    /// </para>
    ///
    /// <para>
    /// Suppressing the <c>tool_result</c> frame along with the status is not a
    /// side effect, it is the same rule: Node's tool loop yields <c>tool_call</c> for
    /// a deferred tool and then <c>continue</c>s without a result, because the real
    /// result belongs to the confirm route — which emits its own <c>tool_result</c>
    /// once the user has actually decided. Two results for one call would render two
    /// outcomes for one action.
    /// </para>
    /// </summary>
    private IEnumerable<AiStreamEvent> Resolve(string callId, JsonElement? result, string? error)
    {
        if (!_announcedCalls.Contains(callId))
        {
            yield break;
        }

        var index = _toolCalls.FindIndex(c => c.CallId == callId);

        if (index >= 0 && AiToolCatalog.RequiresConfirmation(_toolCalls[index].Name))
        {
            yield break;
        }

        if (!_resolvedCalls.Add(callId))
        {
            yield break;
        }

        if (index >= 0)
        {
            _toolCalls[index] = _toolCalls[index] with
            {
                Status = string.IsNullOrEmpty(error) ? "executed" : "failed",
                Result = result,
                Error = error,
            };
        }

        // BOTH keys, always — the unused one explicitly null.
        yield return AiStreamEvents.ToolResult(callId, result, string.IsNullOrEmpty(error) ? null : error);
    }

    private void RememberFallback(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // The `end` frame repeats the whole answer, envelope and all, and a flow
        // with streaming off sends ONLY this. Unwrap it the same way, or the
        // non-streaming path prints the raw JSON the streaming path just stopped
        // printing. Prose is returned unchanged, so a plain-text flow is unaffected.
        var spoken = PlanningEnvelopeReader.ExtractReply(text);
        _fallbackText = spoken.Length > 0 ? spoken : text;
    }
}

/// <summary>
/// One tool call as the translator understood it, for the persisted assistant turn.
/// <paramref name="Args"/> and <paramref name="Result"/> stay as raw JSON so nothing
/// is lost re-shaping a payload whose schema is still moving.
/// </summary>
public readonly record struct TranslatedToolCall(
    string CallId,
    string Name,
    JsonElement? Args,
    string Status,
    JsonElement? Result = null,
    string? Error = null);
