using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// The second implementation behind <see cref="IAiProvider"/>: a Langflow agent,
/// driving the chat stream the frontend already ships against.
///
/// <para>
/// <b>Streaming is the requirement, not an optimisation.</b> Langflow's run endpoint
/// has a blocking form that answers once the whole turn is finished; using it would
/// leave the chat blank for the length of a multi-tool plan and then paint
/// everything at once. This calls <c>?stream=true</c> and translates each frame as it
/// arrives, so tokens and tool pills appear live.
/// </para>
///
/// <para>
/// <b>What it owns and what it does not.</b> It owns the HTTP call, the incremental
/// line read, and the conversation persistence Node does in <c>service.ts</c> —
/// sweep stale pending calls, append the user turn before streaming, append the
/// assistant turn before <c>done</c>. It does NOT own the SSE wire format, the
/// heartbeat, the quota, or the <c>quota</c> frame: those are the route's, exactly as
/// in Node, and keeping the split means the not-configured provider still produces a
/// byte-identical reference response.
/// </para>
///
/// <para>
/// <b>Failures are frames, not statuses.</b> Everything this provider does happens
/// after the route has flushed headers, so a connection refused, a 500 from
/// Langflow, or a timeout must surface as <c>{"type":"error"}</c> inside a 200.
/// The provider throws <see cref="AppException"/> with a real code; the route turns
/// it into the frame.
/// </para>
/// </summary>
public sealed class LangflowAiProvider : IAiProvider
{
    /// <summary>
    /// <c>STALE_PENDING_MS</c>. Matches the conversation sweep window Node uses and
    /// the TTL of the in-memory map it replaces.
    /// </summary>
    public static readonly TimeSpan StalePendingWindow = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _clients;
    private readonly LangflowOptions _options;
    private readonly LangflowInputBinding _binding;
    private readonly AiConversationRepository _conversations;
    private readonly AiGroundingRepository _grounding;
    private readonly TimeProvider _time;

    public LangflowAiProvider(
        IHttpClientFactory clients,
        LangflowOptions options,
        LangflowInputBinding binding,
        AiConversationRepository conversations,
        AiGroundingRepository grounding,
        TimeProvider? time = null)
    {
        _clients = clients;
        _options = options;
        _binding = binding;
        _conversations = conversations;
        _grounding = grounding;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Node's <c>isAiConfigured()</c>, answered from Langflow's own configuration
    /// rather than <c>GEMINI_API_KEY</c>. False keeps every AI route on its 503, which
    /// is the parity target.
    /// </summary>
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// <b>Not provided by this adapter, and deliberately not faked.</b> Node's
    /// transcription is a Gemini audio call; the baseline flow has no audio input and
    /// Langflow's run endpoint takes text. Throwing loudly is the honest answer — a
    /// deployment that configures Langflow and then calls this route has a real gap,
    /// and answering 503 <c>ai_not_configured</c> would misreport it as "switched
    /// off". See the slice report.
    /// </summary>
    public Task<string> TranscribeAsync(
        AiTranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "POST /ai/voice/transcribe has no Langflow counterpart: the run endpoint is text-only and " +
            "the baseline flow has no speech input. Wire a transcription provider, or leave the route " +
            "on NotConfiguredAiProvider.");

    /// <summary>
    /// One turn of <c>POST /ai/ask</c>.
    ///
    /// <para>
    /// The user turn is persisted BEFORE the first frame, so a mid-stream disconnect
    /// still leaves the question in history — the same ordering Node relies on.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<AiStreamEvent> AskAsync(
        AiAskRequest request,
        CancellationToken cancellationToken = default) =>
        RunTurnAsync(
            ObjectId.Parse(request.UserId),
            request.ConversationId,
            request.Question,
            request.AccessToken,
            request.Timezone,
            request.Mode,
            persistUserTurn: true,
            cancellationToken);

    /// <summary>
    /// The post-confirmation continuation.
    ///
    /// <para>
    /// Re-enters the agent with a synthetic report of what the confirmed tool did, so
    /// it can react and finish any remaining steps of the user's original plan. No
    /// new user turn is appended — the "message" here is the tool outcome, and
    /// appending it would show up as something the user typed.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<AiStreamEvent> ContinueAfterConfirmAsync(
        AiContinuationRequest request,
        CancellationToken cancellationToken = default) =>
        RunTurnAsync(
            ObjectId.Parse(request.UserId),
            request.ConversationId,
            ContinuationPrompt(request),
            request.AccessToken,
            request.Timezone,

            // The continuation is always `chat`, whatever started the turn: the
            // prompt here is OUR synthetic report of what the confirmed tool did,
            // not the user's dictation or a scan. Replaying the original mode would
            // tell the agent to re-parse a transcript that no longer exists.
            LangflowInputBinding.ChatMode,
            persistUserTurn: false,
            cancellationToken);

    /// <summary>
    /// What the agent is told about the tool the user just confirmed. Plain text
    /// rather than a structured function-response part, because Langflow's run
    /// endpoint takes a single <c>input_value</c> string — there is no channel for the
    /// <c>functionCall</c>/<c>functionResponse</c> pair Gemini gets. This is the
    /// clearest place where the two orchestrators genuinely do not line up.
    /// </summary>
    private static string ContinuationPrompt(AiContinuationRequest request)
    {
        var outcome = request.ToolError is not null
            ? $"failed with: {request.ToolError}"
            : $"succeeded with result: {Describe(request.ToolResult)}";

        return
            $"The user confirmed the pending action '{request.ToolName}' and it {outcome}. " +
            "Tell them what happened and continue with any remaining steps of their request.";
    }

    private static string Describe(object? value) =>
        value is null ? "{}" : JsonSerializer.Serialize(value, AiStreamJson.Frame);

    // ---- the streaming turn -------------------------------------------------

    private async IAsyncEnumerable<AiStreamEvent> RunTurnAsync(
        ObjectId userId,
        string? conversationId,
        string prompt,
        string? accessToken,
        string? timezone,
        string? mode,
        bool persistUserTurn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Sweep first: a pending call older than the window is declined, so a
        // confirmation card from a previous deploy cannot still fire a bulk delete.
        await _conversations
            .ExpireStalePendingToolCallsAsync(
                userId, AiConversationVocabulary.PersonalScope, StalePendingWindow, scopeId: conversationId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Which conversation generation this turn belongs to, resolved BEFORE the
        // turn touches anything — see SessionIdAsync.
        var sessionId = await SessionIdAsync(userId, conversationId, cancellationToken).ConfigureAwait(false);

        if (persistUserTurn)
        {
            await AppendAsync(userId, conversationId, "user", prompt, null, cancellationToken).ConfigureAwait(false);
        }

        var translator = new LangflowEventTranslator();
        yield return translator.Start();

        await foreach (var frame in ReadFramesAsync(
                               userId, sessionId, prompt, accessToken, timezone, mode, cancellationToken)
                           .ConfigureAwait(false))
        {
            foreach (var translated in translator.Accept(frame))
            {
                yield return translated;
            }
        }

        // Did the answer claim work nothing did? See FabricatedActionGuard: a turn
        // that says "Added it." and wrote nothing is the one failure here that no
        // other surface will ever contradict.
        var fabricated = FabricatedActionGuard.FirstUnaccounted(translator.Claims, translator.ToolCalls);

        if (fabricated is null)
        {
            await AppendAsync(
                    userId,
                    conversationId,
                    "assistant",
                    translator.AssistantText,
                    translator.ToolCalls,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            translator.WithholdAnswer();

            // The prose is dropped; the tool calls are NOT. Whatever really ran is
            // true and has to stay — dropping a pending_confirmation record here
            // would 404 the confirm button on a card the user is looking at. A turn
            // with no true part left is not persisted at all: an empty bubble in the
            // history would be its own small lie about having answered.
            if (translator.ToolCalls.Count > 0)
            {
                await AppendAsync(
                        userId, conversationId, "assistant", string.Empty, translator.ToolCalls, cancellationToken)
                    .ConfigureAwait(false);
            }

            yield return AiStreamEvents.Error(
                FabricatedActionGuard.ErrorCode,
                FabricatedActionGuard.ErrorMessage);
        }

        foreach (var tail in translator.Complete())
        {
            yield return tail;
        }
    }

    /// <summary>
    /// The HTTP half: POST the run request, then hand back one
    /// <see cref="LangflowFrame"/> per line as the body arrives.
    ///
    /// <para>
    /// <c>ResponseHeadersRead</c> is what makes this incremental — the default
    /// buffers the whole body first, which would silently reintroduce the blocking
    /// behaviour this adapter exists to avoid.
    /// </para>
    /// </summary>
    /// <summary>
    /// The <c>session_id</c> this turn runs under: the caller's id joined to the
    /// conversation's current GENERATION.
    ///
    /// <para>
    /// <b>Why not just the user id.</b> That is what this sent originally, and it is a
    /// permanent per-user session: Langflow keeps its own memory against the key, so
    /// <c>POST /ai/conversation/reset</c> — which only clears OUR messages — left the
    /// agent still answering from everything before it. Two things followed, both
    /// user-visible. The reset button lied. And re-asking a question inside that
    /// immortal session came back as a memorised envelope with no tool call at all:
    /// the agent replayed its previous answer instead of acting, which reads exactly
    /// like it hallucinating a completed action.
    /// </para>
    ///
    /// <para>
    /// <b>Read once per turn, at the top.</b> The generation must not move underneath
    /// a turn, and reading it before the user message is appended anchors the turn to
    /// the conversation as it was when the user asked. A continuation after a
    /// confirmation reads the same unchanged generation, which is what keeps a
    /// multi-step plan in one session.
    /// </para>
    /// </summary>
    private async Task<string> SessionIdAsync(
        ObjectId userId,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations
            .LoadAsync(userId, AiConversationVocabulary.PersonalScope, conversationId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return AgentSessionId.For(userId, conversation.SessionGeneration);
    }

    private async IAsyncEnumerable<LangflowFrame> ReadFramesAsync(
        ObjectId userId,
        string sessionId,
        string prompt,
        string? accessToken,
        string? timezone,
        string? mode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = _clients.CreateClient(LangflowOptions.HttpClientName);
        client.Timeout = _options.Timeout;

        // Read the user's matters BEFORE the run, on every turn — Node's
        // contextBuilder does the same and accepts the query cost even on small talk.
        // The alternative it rejects is worth repeating: the agent holds the full tool
        // surface at all times, so a turn that reaches deleteTask/updateTask with no
        // grounded ids in front of it invents one.
        var tasks = await _grounding
            .ListForPromptAsync(userId, TaskGrounding.PromptStatuses, TaskGrounding.TaskCap, cancellationToken)
            .ConfigureAwait(false);

        using var request = BuildRequest(
            sessionId, prompt, accessToken, timezone, mode, TaskGrounding.BuildTaskBlock(tasks));

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new AppException(502, "langflow_unavailable", $"Could not reach the agent: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new AppException(
                    502,
                    "langflow_error",
                    $"The agent returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? pendingEventName = null;

            // Loop on ReadLineAsync's own null-at-EOF rather than checking
            // EndOfStream: that property BLOCKS to fill the buffer, which on a stream
            // deliberately held open between tokens is a synchronous wait on the
            // network inside an async read (CA2024).
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (LangflowWireContract.TryParseLine(line, ref pendingEventName, out var frame))
                {
                    yield return frame;
                }
            }
        }
    }

    private HttpRequestMessage BuildRequest(
        string sessionId,
        string prompt,
        string? accessToken,
        string? timezone,
        string? mode,
        string myTasks)
    {
        // WHERE the prompt goes is LangflowInputBinding's business, not this file's —
        // a flow with no ChatInput takes it as a tweak instead, and which is which is
        // configuration. See that type for why posting input_value to the wrong flow
        // fails silently rather than loudly.
        var payload = _binding.BuildRequest(
            prompt,

            // `<userId>:<conversation generation>`. The user half is what keeps
            // Langflow's per-session memory from crossing accounts; the generation
            // half is what a reset rotates, so the agent cannot answer out of a
            // conversation the user has cleared. See SessionIdAsync.
            sessionId,
            accessToken,
            _time.GetUtcNow(),

            // The caller's zone. Without it `currentDate` goes out offset-free and
            // the agent silently assumes +00:00, putting every dueAt it derives out
            // by the user's whole UTC offset.
            timezone,

            // Which entry path this turn came from. The flow branches on it, so a
            // dictated sentence is parsed as a transcript rather than answered as
            // chat. Absent ⇒ chat, which is every caller that predates this.
            mode: string.IsNullOrWhiteSpace(mode) ? LangflowInputBinding.ChatMode : mode,

            // The user's existing matters, with their real ids. Without this the
            // agent cannot see what it already filed and re-creates it.
            myTasks: myTasks);

        var request = new HttpRequestMessage(HttpMethod.Post, _options.RunUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AiStreamJson.Frame),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        }

        return request;
    }

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";

    // ---- persistence --------------------------------------------------------

    private Task AppendAsync(
        ObjectId userId,
        string? conversationId,
        string role,
        string text,
        IReadOnlyList<TranslatedToolCall>? toolCalls,
        CancellationToken cancellationToken)
    {
        var message = new AiConversationMessageDocument
        {
            Role = role,
            Text = text,
            CreatedAt = DateTime.UtcNow,

            // `default: undefined` in Mongoose — an empty list is stored as nothing
            // at all, and the ROUTE coerces the absent value back to [] on read.
            ToolCalls = toolCalls is { Count: > 0 } ? toolCalls.Select(ToDocument).ToList() : null,
        };

        return _conversations.AppendTurnAsync(
            userId,
            AiConversationVocabulary.PersonalScope,
            conversationId,
            message,
            cancellationToken: cancellationToken);
    }

    private static AiConversationToolCallDocument ToDocument(TranslatedToolCall call) => new()
    {
        CallId = call.CallId,
        Name = call.Name,
        Args = ToBson(call.Args) ?? new BsonDocument(),
        Status = call.Status,
        Result = ToBson(call.Result) ?? BsonNull.Value,
        Error = call.Error,
    };

    /// <summary>
    /// Raw JSON → BSON for storage. <c>args</c> is <c>Schema.Types.Mixed</c>, so any
    /// shape is permitted and none is coerced — narrowing it here is what made one
    /// oddly-shaped stored value turn every read of a user's history into a 500.
    /// </summary>
    private static BsonValue? ToBson(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return BsonDocument.TryParse(
            value.ValueKind == JsonValueKind.Object ? value.GetRawText() : $"{{\"value\":{value.GetRawText()}}}",
            out var parsed)
            ? value.ValueKind == JsonValueKind.Object ? parsed : parsed["value"]
            : BsonNull.Value;
    }
}
