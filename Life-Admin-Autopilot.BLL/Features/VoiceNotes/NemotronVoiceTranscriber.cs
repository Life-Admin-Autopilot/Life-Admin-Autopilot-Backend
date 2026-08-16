using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// The real audio → text seam: <see cref="IVoiceTranscriber"/> over the Nemotron ASR
/// the rest of the app already transcribes through
/// (<c>ISpeechToTextService</c> → <c>NemotronTranscriptionService</c>, HF_TOKEN).
///
/// <para>
/// <b>Replaces <see cref="NullVoiceTranscriber"/></b>, which throws 503 on its first
/// line and is the reason every voice note used to burn its whole attempt ladder and
/// settle at <c>failed</c>. That was an honest reproduction of Node without
/// <c>GEMINI_API_KEY</c>; it stops being the target the moment there is a real
/// provider to call.
/// </para>
///
/// <para>
/// <b>Why not the Gemini audio call Node makes.</b> Nemotron is a real ASR: it
/// transcribes what was said instead of paraphrasing it, and it keeps Egyptian
/// dialect rather than flattening it into Modern Standard Arabic. It is also already
/// wired, already retried, and already has the bilingual repair policy this product
/// needs — see <c>SpeechToTextService</c>'s detect-first/pin-second comment, which
/// records four consecutive real recordings where pinning to the UI locale got 0/4
/// usable transcripts and auto-detect got 4/4.
/// </para>
/// </summary>
public sealed class NemotronVoiceTranscriber : IVoiceTranscriber
{
    private readonly ISpeechToTextService _speech;
    private readonly ILogger<NemotronVoiceTranscriber> _logger;

    public NemotronVoiceTranscriber(ISpeechToTextService speech, ILogger<NemotronVoiceTranscriber> logger)
    {
        _speech = speech;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(
        VoiceTranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        // The stored mimeType cannot be trusted to name the container — see
        // VoiceAudioFormat. The provider takes a `data:{type};base64,…` URI, so the
        // type it is given is the type it decodes as.
        var contentType = VoiceAudioFormat.Sniff(request.Bytes, request.MimeType);

        var result = await _speech
            .TranscribeStoredAsync(request.Bytes, contentType, request.Locale, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return result.Transcript;
        }

        // Silence is NOT a failure, and this is the one mapping that matters most.
        // The provider reports "I heard nothing" as an error because for the chat
        // surface it is a dead end; for a background note it means the recording had
        // no speech in it, and the worker's own contract is that an empty transcript
        // short-circuits extraction and settles the note as `ready`. Letting
        // ASR_EMPTY_TRANSCRIPT escape as an exception would instead retry the same
        // silent audio four times and then mark the note failed — telling the user
        // their capture broke when in fact nobody spoke.
        if (result.ErrorCode == SpeechErrorCodes.EmptyTranscript)
        {
            _logger.LogInformation("voiceNote:no-speech bytes={ByteCount}", request.Bytes.Length);
            return string.Empty;
        }

        // Everything else is thrown, because the worker's retry classifier is what
        // decides whether it is worth another attempt — and it reads the STATUS. A
        // rate limit or an outage has to come back as a transient 5xx so the note
        // waits and retries; unreadable audio has to come back as a 4xx so it fails
        // fast instead of spending four attempts proving the file is still corrupt.
        throw new AppException(
            StatusFor(result.ErrorCode),
            result.ErrorCode ?? "asr_failed",
            result.ErrorMessage ?? "Transcription failed.");
    }

    /// <summary>
    /// The ASR code, in the terms <see cref="VoiceNoteRetryPolicy.IsTransient"/>
    /// understands.
    ///
    /// <para>
    /// <c>IsClientError</c> already draws the line the API layer draws, so it is
    /// reused rather than re-listed — a code added there must not silently become
    /// retryable here. The two transient statuses are split apart because 429 and
    /// 503 mean different things to a human reading the log even though the
    /// classifier treats them alike.
    /// </para>
    ///
    /// <para>
    /// <c>ASR_QUOTA_EXCEEDED</c> is deliberately NOT transient: the account's
    /// included credits are gone and waiting does not help, so retrying just burns
    /// the ladder before reporting the same thing.
    /// </para>
    /// </summary>
    private static int StatusFor(string? errorCode) => errorCode switch
    {
        SpeechErrorCodes.RateLimited => 429,
        SpeechErrorCodes.Timeout or SpeechErrorCodes.Unavailable or SpeechErrorCodes.NetworkError => 503,
        SpeechErrorCodes.QuotaExceeded or SpeechErrorCodes.NotAuthorized or SpeechErrorCodes.NotConfigured => 502,
        not null when SpeechErrorCodes.IsClientError(errorCode) => 400,
        _ => 502,
    };
}
