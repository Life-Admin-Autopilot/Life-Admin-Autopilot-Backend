using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>One pre-resolved answer the extractor proposed for a clarifiable item.</summary>
public sealed record DraftClarifyOption(string Label, DateTime? DueAt = null);

/// <summary>
/// A clarifiable hold the extractor surfaced: the question to ask plus
/// pre-resolved option dates, already normalised to absolute instants.
/// </summary>
/// <param name="CostOfWrong">
/// Whether a wrong guess is cheap to fix (reschedule a nudge) or expensive (miss a
/// bill, a flight, a court date). Decides whether the task staged alongside the
/// question gets a live reminder or a withheld one.
/// </param>
public sealed record DraftClarification(
    string Question,
    string Kind,
    string CostOfWrong,
    IReadOnlyList<DraftClarifyOption> Options);

/// <summary>
/// A hardened item the extractor returns — domain validated, date resolved. NO KEY
/// yet: the note-scoped key is assigned by <see cref="VoiceItemGate"/> so retries
/// stay idempotent.
/// </summary>
public sealed record DraftVoiceItem(
    string Title,
    string Domain,
    string Priority,
    string Confidence,
    string ReviewReason,
    IReadOnlyList<string> Reasons,
    int? EstimateMinMinutes = null,
    int? EstimateMaxMinutes = null,
    string? DueRaw = null,
    DateTime? DueAt = null,
    string? Notes = null,
    DraftClarification? Clarification = null);

public sealed record VoiceExtractionRequest(string Transcript, string? Timezone, string? Locale);

/// <summary>
/// The transcript → structured items seam.
/// <c>modules/ai/voiceCore/extract.ts</c> lives behind this.
/// </summary>
public interface IVoiceExtractor
{
    Task<IReadOnlyList<DraftVoiceItem>> ExtractAsync(
        VoiceExtractionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The no-key implementation. Same reasoning as <see cref="NullVoiceTranscriber"/>
/// — <c>getGeminiClient()</c> throws 503 before any model call.
///
/// <para>
/// Reachable only through <c>POST /me/voice-notes/{id}/extract-tasks</c> on a note
/// that ALREADY has a transcript. Without a key the worker can never store one, so
/// on the parity target that route always stops earlier, at
/// <c>400 voice_note_not_ready</c>. When it is reachable the 503 surfaces as a
/// <c>500 internal_error</c>, which is what the contract records for that path.
/// </para>
/// </summary>
public sealed class NullVoiceExtractor : IVoiceExtractor
{
    public Task<IReadOnlyList<DraftVoiceItem>> ExtractAsync(
        VoiceExtractionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new AppException(503, "ai_not_configured", NullVoiceTranscriber.NotConfiguredMessage);
}
