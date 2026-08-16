using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>Inputs for a one-shot chat-voice transcription.</summary>
/// <param name="Bytes">Raw audio, already size-checked by the route.</param>
/// <param name="MimeType">Content type up to the first <c>;</c>; defaults to <c>audio/m4a</c>.</param>
/// <param name="Locale">A HINT to the recogniser. Never an instruction to translate.</param>
public readonly record struct AiTranscriptionRequest(byte[] Bytes, string MimeType, string Locale);

/// <summary>Inputs for one turn of <c>POST /ai/ask</c>.</summary>
/// <param name="UserId">Owner, as the 24-hex string the Node service passes around.</param>
/// <param name="Question">Already trimmed and length-checked.</param>
/// <param name="Timezone">IANA zone anchoring relative phrases. UTC when absent.</param>
public readonly record struct AiAskRequest(string UserId, string Question, string? Timezone)
{
    /// <summary>
    /// The caller's own bearer token, verbatim and without the <c>Bearer </c> prefix.
    ///
    /// <para>
    /// <b>Added as an init-only property, deliberately not a fourth positional
    /// parameter</b>, so every existing construction site keeps compiling — this is an
    /// extension of the seam, not a redesign of it.
    /// </para>
    ///
    /// <para>
    /// It exists because an agent that runs tools by calling this API back needs to
    /// act AS the user; the alternative is a service credential with authority over
    /// every account, which is strictly worse. A provider that does not call back
    /// (Gemini, and Langflow flows whose tools talk to Mongo directly) simply ignores
    /// it. Null when the route could not read one.
    /// </para>
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// Which entry path produced this turn: <c>chat</c>, <c>transcript</c>,
    /// <c>document</c> or <c>clarification</c>. Null ⇒ <c>chat</c>.
    ///
    /// <para>
    /// Init-only for the same reason as <see cref="AccessToken"/> — every existing
    /// construction site keeps compiling. A provider that has no notion of modes
    /// (Gemini) ignores it; the Langflow flow branches on it, because typed,
    /// dictated and extracted text want different handling of the same sentence.
    /// </para>
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Which conversation thread this turn belongs to.
    ///
    /// <para>
    /// Init-only for the same reason as <see cref="AccessToken"/> — every existing
    /// construction site keeps compiling. Null ⇒ the legacy single-thread document
    /// (<c>scopeId: null</c>), which is exactly what a client that predates
    /// multi-conversation support still gets.
    /// </para>
    ///
    /// <para>
    /// It also selects the AGENT's memory, not just ours: the Langflow session key
    /// derives from the thread document's own session generation, so two threads
    /// cannot answer out of each other's history.
    /// </para>
    /// </summary>
    public string? ConversationId { get; init; }
}

/// <summary>Inputs for the post-confirmation continuation of a turn.</summary>
public readonly record struct AiContinuationRequest(
    string UserId,
    string CallId,
    string ToolName,
    object? ToolArgs,
    object? ToolResult,
    string? ToolError,
    string? Timezone)
{
    /// <summary>See <see cref="AiAskRequest.AccessToken"/>. Same value, same reasons.</summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// Which conversation thread this turn belongs to.
    ///
    /// <para>
    /// Init-only for the same reason as <see cref="AccessToken"/> — every existing
    /// construction site keeps compiling. Null ⇒ the legacy single-thread document
    /// (<c>scopeId: null</c>), which is exactly what a client that predates
    /// multi-conversation support still gets.
    /// </para>
    ///
    /// <para>
    /// It also selects the AGENT's memory, not just ours: the Langflow session key
    /// derives from the thread document's own session generation, so two threads
    /// cannot answer out of each other's history.
    /// </para>
    /// </summary>
    public string? ConversationId { get; init; }
}

/// <summary>
/// <b>The one seam Gemini sits behind.</b>
///
/// <para>
/// In Node the model client is reached through
/// <c>modules/ai/provider/geminiClient.ts</c>, and <c>isAiConfigured()</c> —
/// literally <c>Boolean(env().GEMINI_API_KEY)</c> — is the switch every AI route
/// consults. This interface is that switch plus the three calls the routes would
/// make once a model is wired.
/// </para>
///
/// <para>
/// <b>Inference is deliberately deferred to the Langflow phase.</b> The shipped
/// implementation is <see cref="NotConfiguredAiProvider"/>, whose
/// <see cref="IsConfigured"/> is false on the parity target, so no route ever
/// reaches the three model calls. Replacing this ONE registration — with
/// <c>services.Replace(...)</c>, per KERNEL.md §8.5 — is the whole of wiring a
/// model in.
/// </para>
///
/// <para>
/// <b>The 503 message is NOT this interface's business.</b> The API carries EIGHT
/// distinct <c>ai_not_configured</c> message strings across the routes that consult
/// this switch, so each route builds its own <c>AppException</c> from its own
/// literal. A provider that threw a single shared 503 would collapse them.
/// </para>
/// </summary>
public interface IAiProvider
{
    /// <summary>Node's <c>isAiConfigured()</c>.</summary>
    bool IsConfigured { get; }

    /// <summary><c>modules/ai/audioTranscriber.ts</c> — bytes in, text out, no DB writes.</summary>
    Task<string> TranscribeAsync(AiTranscriptionRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>modules/ai/service.ts</c> <c>ask()</c> — the event generator behind the SSE stream.</summary>
    IAsyncEnumerable<AiStreamEvent> AskAsync(AiAskRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>modules/ai/service.ts</c> <c>continueAfterConfirm()</c>.</summary>
    IAsyncEnumerable<AiStreamEvent> ContinueAfterConfirmAsync(
        AiContinuationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One frame's payload. The route renames the generator's <c>kind</c> key to
/// <c>type</c> and writes <c>data: {json}\n\n</c>; see
/// <c>docs/contract/paths.integrations.yaml</c> <c>AiStreamEvent</c> for the eight
/// variants. Modelled as a loose payload because the streaming half is not built
/// yet and pinning a shape now would only have to be re-derived.
///
/// <para>
/// Seven of the eight can come from a provider and are listed below. The eighth,
/// <c>conversation</c>, is the route's alone — it names the thread the turn landed
/// in, which no provider knows — so it never travels through this struct. See
/// <c>AiStreamEvents</c>.
/// </para>
/// </summary>
/// <param name="Kind">
/// <c>sources</c> | <c>tool_call</c> | <c>tool_result</c> | <c>token</c> |
/// <c>done</c> | <c>error</c> | <c>quota</c>.
/// </param>
/// <param name="Payload">The remaining keys, in emission order.</param>
public readonly record struct AiStreamEvent(string Kind, IReadOnlyDictionary<string, object?> Payload);

/// <summary>
/// The shipped provider: <b>no model, and honest about it</b>.
///
/// <para>
/// <see cref="IsConfigured"/> reads the same configuration value slice C's
/// <c>AiAvailability</c> reads, so the two agree on every route. On the parity
/// target (<c>GEMINI_API_KEY</c> empty) it is false and every AI route
/// short-circuits to its own 503 before touching this object.
/// </para>
///
/// <para>
/// The three model calls throw a plain <see cref="NotSupportedException"/> rather
/// than a 503, deliberately: they are reachable only when someone HAS configured a
/// key, and a deployment in that state should fail loudly instead of quietly
/// pretending the feature is switched off. Same choice slice C made in
/// <c>AiUnavailable.NotWiredHere</c>.
/// </para>
/// </summary>
public sealed class NotConfiguredAiProvider : IAiProvider
{
    private readonly bool _configured;

    public NotConfiguredAiProvider(IConfiguration configuration)
    {
        var key = configuration["GEMINI_API_KEY"] ?? configuration["Ai:GeminiApiKey"];
        _configured = !string.IsNullOrEmpty(key);
    }

    public bool IsConfigured => _configured;

    public Task<string> TranscribeAsync(
        AiTranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotWired("POST /ai/voice/transcribe");

    public IAsyncEnumerable<AiStreamEvent> AskAsync(
        AiAskRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotWired("POST /ai/ask");

    public IAsyncEnumerable<AiStreamEvent> ContinueAfterConfirmAsync(
        AiContinuationRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotWired("POST /ai/tools/confirm/{callId}");

    private static Exception NotWired(string route) =>
        new NotSupportedException(
            $"{route} needs a model call. Inference is deferred to the Langflow phase: replace the " +
            $"{nameof(IAiProvider)} registration rather than editing the route.");
}
