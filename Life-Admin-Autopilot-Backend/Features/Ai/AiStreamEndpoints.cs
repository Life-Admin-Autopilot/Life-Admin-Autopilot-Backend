using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// The three routes that reach the model — <c>/ai/ask</c>,
/// <c>/ai/tools/confirm/{callId}</c> and <c>/ai/voice/transcribe</c>.
///
/// <para>
/// <b>Everything before the model is real and ported; the streaming half is
/// deliberately not built.</b> On the parity target <c>IAiProvider.IsConfigured</c>
/// is false, so <c>/ai/ask</c> and <c>/ai/voice/transcribe</c> answer 503 and
/// <c>/ai/tools/confirm</c> answers 404 — the stream is never opened and the
/// contract for these three routes is entirely pre-stream. Inference, and with it
/// SSE, belong to the Langflow phase; the seam is <see cref="IAiProvider"/> and
/// nothing else in this file needs to change when it lands.
/// </para>
///
/// <para>
/// <b>THE RULE that governs the deferred half:</b> a failure BEFORE the stream
/// opens is an ordinary JSON HTTP error with a real status code; a failure AFTER it
/// opens is a <c>{"type":"error"}</c> frame inside a 200 <c>text/event-stream</c>.
/// The status line is committed the moment the headers flush.
/// </para>
///
/// <para>
/// <b>The gate order differs per route and is observable.</b> <c>/ai/ask</c>
/// validates the body BEFORE checking availability, so a bad payload is a 400.
/// <c>/ai/voice/transcribe</c> checks availability FIRST, so an 11 MB body is a 503
/// and not a 400. <c>/ai/tools/confirm</c> has no availability gate at all.
/// </para>
/// </summary>
public static class AiStreamEndpoints
{
    /// <summary>
    /// <c>CHAT_AUDIO_MAX_BYTES</c> — 6 MiB, hard-coded in the Node route, NOT an env
    /// var (contrast <c>VOICE_NOTE_MAX_BYTES</c>).
    /// </summary>
    private const int ChatAudioMaxBytes = 6 * 1024 * 1024;

    /// <summary>The four content types <c>express.raw()</c> is configured to consume.</summary>
    private static readonly string[] RawAudioTypes =
    {
        "audio/m4a", "audio/mp4", "audio/aac", "application/octet-stream",
    };

    public static IEndpointRouteBuilder MapAiStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapAsk(endpoints);
        MapConfirm(endpoints);
        MapTranscribe(endpoints);
        return endpoints;
    }

    // ---- POST /ai/ask ------------------------------------------------------

    private static void MapAsk(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/ai/ask", async (
            HttpContext context,
            IAiProvider ai,
            AiQuotaService quota,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // 3. Body first — a malformed payload reports what is wrong with it
            //    rather than masking as the availability 503.
            var body = await AiRequests
                .ReadAsync<AskBody>(context, KernelBodyOptions.Lenient("Invalid ask payload."), cancellationToken)
                .ConfigureAwait(false);

            var issues = new BodyIssues();
            var question = AiRequests.ReadQuestion(issues, body.Question);
            AiRequests.ReadEnum(issues, body.Scope, "scope", AiRequests.Scopes, fallback: "personal", required: false);
            var timezone = AiRequests.ReadTimezone(issues, body.Timezone);
            issues.ThrowIfInvalid("invalid_body", "Invalid ask payload.");

            // 4. Availability.
            if (!ai.IsConfigured)
            {
                throw AiShellErrors.Ask();
            }

            // 5. ATOMIC RESERVE, before any expensive work and before the headers
            //    flush, so the 402 lands as an ordinary JSON error rather than an
            //    error frame. Whoever consumes the slot owes exactly one release if
            //    the turn never reaches `done` — AiStreamTurns owns that.
            var tier = AiQuotaService.ResolveTier();
            await quota.AdmitAsync(caller.Id, tier, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 6. Headers flush — from here on, only frames.
            await AiStreamTurns
                .RunAskAsync(
                    context,
                    caller,
                    ai,
                    quota,
                    tier,
                    new AiAskRequest(caller.IdString, question!, timezone, AiRequests.BearerToken(context)),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Empty;
        })
        .RequireAuthorization()
        .RateLimited(KernelRateLimiters.AiAsk);

    // ---- POST /ai/tools/confirm/{callId} -----------------------------------

    private static void MapConfirm(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/ai/tools/confirm/{callId}", async (
            string callId,
            HttpContext context,
            IAiProvider ai,
            AiQuotaService quota,
            AiConversationService conversations,
            AiConfirmedToolRunner tools,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await AiRequests
                .ReadAsync<ConfirmToolBody>(
                    context,
                    KernelBodyOptions.Lenient("Invalid confirm payload."),
                    cancellationToken)
                .ConfigureAwait(false);

            var issues = new BodyIssues();
            var action = AiRequests.ReadEnum(
                issues, body.Action, "action", AiRequests.ConfirmActions, fallback: null, required: true);
            issues.ThrowIfInvalid("invalid_body", "Invalid confirm payload.");

            // NO availability gate on this route — isAiConfigured() is consulted
            // only mid-stream, to decide whether to run a follow-up turn, and even
            // then it emits `{"type":"done","usage":{}}` rather than an error. With
            // no key no pending call can ever have been created, so every call
            // lands on the durable-record lookup below and 404s. Verified live.
            var call = await conversations
                .RequirePendingToolCallAsync(caller.Id, callId, cancellationToken)
                .ConfigureAwait(false);

            // RE-VALIDATE ON RECOVERY. These args came back out of the database —
            // possibly written by an older build, possibly an hour and a deploy ago —
            // and they are about to drive a destructive dispatch. Re-parsing here is
            // defence in depth, and it is deliberately BEFORE the flush so a stale
            // record is an ordinary 400 rather than an error frame nobody reads.
            //
            // It also strips the display-only `count` the confirmation card carries,
            // which must never reach the bulk filter.
            var toolArgs = AiToolCatalog.ValidateArgs(call.Name, call.Args);

            // Node ALSO peeks an in-memory map here, for two things: a cross-user
            // ownership check, and the caller's timezone, which the record does not
            // carry. Neither is modelled.
            //
            // The 403 `cross_user_confirm` is unreachable even in Node — the durable
            // lookup above is already user-scoped, so another user's callId 404s
            // before the map is consulted. The timezone is simply lost, exactly as it
            // is on the reference after any restart; the record is the source of
            // truth and a cache that survives nothing is not one.
            await AiStreamTurns
                .RunConfirmAsync(
                    context,
                    caller,
                    ai,
                    quota,
                    conversations,
                    tools,
                    AiQuotaService.ResolveTier(),
                    action!,
                    callId,
                    call.Name,
                    toolArgs,
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Empty;
        })
        .RequireAuthorization()
        .RateLimited(KernelRateLimiters.AiConfirm);

    // ---- POST /ai/voice/transcribe -----------------------------------------

    private static void MapTranscribe(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/ai/voice/transcribe", async (
            HttpContext context,
            IAiProvider ai,
            AiQuotaService quota,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // express.raw() runs as MIDDLEWARE, before the handler, and only for the
            // four audio content types. Its ceiling is DOUBLE the friendly cap, so a
            // body over 12 MiB never reaches the handler and surfaces as 500 — which
            // is why the read happens here, ahead of the availability gate, and only
            // when the content type matches. Verified live: 13 MB as `audio/m4a` is
            // a 500, the same 13 MB as `text/plain` is the 503 below.
            var bytes = IsRawAudio(context.Request)
                ? await KernelBody
                    .ReadBytesAsync(context.Request, ChatAudioMaxBytes * 2, cancellationToken)
                    .ConfigureAwait(false)
                : Array.Empty<byte>();

            // a. THE ODD ONE OUT: availability is checked BEFORE the payload checks,
            //    so an empty or 11 MB body still answers 503 rather than 400.
            //    Verified live. Note the message OMITS the trailing " to enable."
            //    that /ai/ask carries.
            if (!ai.IsConfigured)
            {
                throw AiShellErrors.Transcribe();
            }

            // b. + c. The friendly ceilings, in Node's order.
            if (bytes.Length == 0)
            {
                throw AppException.BadRequest("empty_body", "No audio payload received.");
            }

            if (bytes.Length > ChatAudioMaxBytes)
            {
                throw AppException.BadRequest("payload_too_large", "Recording exceeds 6MB. Try a shorter clip.");
            }

            var mimeType = MimeTypeOf(context.Request);

            // d. Reserve before the paid call; e. refund if it throws, so a provider
            //    error does not burn the user's quota.
            var tier = AiQuotaService.ResolveTier();
            await quota.AdmitAsync(caller.Id, tier, cancellationToken: cancellationToken).ConfigureAwait(false);

            string text;
            try
            {
                text = await ai
                    .TranscribeAsync(
                        new AiTranscriptionRequest(bytes, mimeType, AiLocaleHint(context)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await quota.ReleaseAsync(caller.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
                throw;
            }

            return Results.Ok(new AiTranscribeResponse { Text = text });
        })
        .RequireAuthorization()
        .RateLimited(KernelRateLimiters.AiVoice);

    /// <summary>
    /// Would <c>express.raw({ type: [...] })</c> consume this body? Anything else —
    /// including an absent header — leaves the body unread, so the size ceilings
    /// never fire.
    /// </summary>
    private static bool IsRawAudio(HttpRequest request)
    {
        var header = request.Headers.ContentType.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var media = header.Split(';')[0].Trim();
        return RawAudioTypes.Contains(media, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>(req.header('content-type') ?? 'audio/m4a').split(';')[0]?.trim() || 'audio/m4a'</c>.
    /// </summary>
    private static string MimeTypeOf(HttpRequest request)
    {
        var media = request.Headers.ContentType.ToString().Split(';')[0].Trim();
        return media.Length == 0 ? "audio/m4a" : media;
    }

    /// <summary>
    /// <c>getUserAiLocale(auth.sub)</c> reads the caller's profile. Resolving it is
    /// only worth a round trip once a provider exists to consume the hint, so this
    /// stays at the default until the Langflow phase wires the read.
    /// </summary>
    private static string AiLocaleHint(HttpContext context)
    {
        _ = context;
        return "en";
    }
}

/// <summary>The transcribe 200 body. One key.</summary>
public sealed class AiTranscribeResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
