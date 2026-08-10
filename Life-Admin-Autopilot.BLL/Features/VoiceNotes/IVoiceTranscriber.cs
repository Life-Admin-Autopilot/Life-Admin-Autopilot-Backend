using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <param name="Bytes">The stored audio, read back from <c>IVoiceNoteStorage</c>.</param>
/// <param name="MimeType">The note's stored type, defaulting to <c>audio/m4a</c>.</param>
/// <param name="Locale">
/// A recognition HINT, never an instruction to translate. Node is emphatic about
/// this: the transcript is the input to extraction, which is where the user's
/// language gets applied, and a name heard in one language and rendered in
/// another is unrecoverable.
/// </param>
public sealed record VoiceTranscriptionRequest(byte[] Bytes, string MimeType, string? Locale);

/// <summary>
/// The audio → text seam. <c>modules/ai/audioTranscriber.ts</c> lives behind this,
/// and the voice-note slice deliberately knows nothing about Gemini.
/// </summary>
public interface IVoiceTranscriber
{
    /// <summary>
    /// Returns the empty string for silence — Node maps both an empty response and
    /// the literal <c>(no speech detected)</c> to <c>''</c>, and the worker then
    /// skips extraction rather than treating it as a failure.
    /// </summary>
    Task<string> TranscribeAsync(VoiceTranscriptionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The no-key implementation, and the CURRENT PARITY TARGET.
///
/// <para>
/// Node's <c>transcribeAudio</c> calls <c>getGeminiClient()</c> on its FIRST line,
/// before its own retry loop, so without <c>GEMINI_API_KEY</c> it throws
/// <c>503 ai_not_configured</c> immediately and never reaches the model. The
/// worker's retry classifier treats 503 as transient, burns the whole attempt
/// ladder, and the note settles at <c>failed</c> with this exact sentence in
/// <c>failureReason</c>. Reproducing that honestly is the job — a stub that
/// fabricated a transcript would green a screen the reference server cannot
/// produce.
/// </para>
///
/// <para>
/// The AI slice REPLACES this registration (<c>services.Replace(...)</c>, not
/// <c>TryAdd</c>) when a real provider lands.
/// </para>
/// </summary>
public sealed class NullVoiceTranscriber : IVoiceTranscriber
{
    /// <summary>Verbatim from <c>modules/ai/provider/geminiClient.ts</c>.</summary>
    public const string NotConfiguredMessage =
        "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.";

    public Task<string> TranscribeAsync(
        VoiceTranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new AppException(503, "ai_not_configured", NotConfiguredMessage);
}
