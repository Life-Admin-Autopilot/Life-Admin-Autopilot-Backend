using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// One pre-resolved answer the extractor proposed for a clarifiable item.
/// </summary>
/// <param name="Label">
/// English, and a FALLBACK only. See <paramref name="LabelKey"/>.
/// </param>
/// <param name="LabelKey">
/// An i18n key into the client's <c>uncertainty</c> catalogue, with
/// <paramref name="LabelParams"/> as its values. Null on a chip whose text is
/// already the user's own words, and on every row written before this existed.
/// </param>
public sealed record DraftClarifyOption(
    string Label,
    DateTime? DueAt = null,
    string? LabelKey = null,
    IReadOnlyDictionary<string, string>? LabelParams = null);

/// <summary>
/// A clarifiable hold the extractor surfaced: the question to ask plus
/// pre-resolved option dates, already normalised to absolute instants.
/// </summary>
/// <param name="CostOfWrong">
/// Whether a wrong guess is cheap to fix (reschedule a nudge) or expensive (miss a
/// bill, a flight, a court date). Decides whether the task staged alongside the
/// question gets a live reminder or a withheld one.
/// </param>
/// <param name="Question">
/// English, and a FALLBACK only. See <paramref name="QuestionKey"/>.
/// </param>
/// <param name="QuestionKey">
/// <b>Why a key and not a sentence.</b> Unlike the chat lane, where the model
/// writes the question in the language of the message it is answering, these
/// sentences are composed by the SERVER — voice is fire-and-forget and the AI
/// never asks during the interaction (<c>VoiceAutoFilePolicy</c>). Composed
/// server-side they were composed in English, so an Arabic note produced an
/// English card sitting inside Arabic chrome. Sending a key instead lets the
/// card render in whatever language the app is in AT READING TIME, which also
/// survives the user switching language after the question was raised, and lets
/// the dates inside it be formatted by the client's own locale-aware formatters
/// rather than <c>ToString("dddd d MMMM")</c>.
/// </param>
public sealed record DraftClarification(
    string Question,
    string Kind,
    string CostOfWrong,
    IReadOnlyList<DraftClarifyOption> Options,
    string? QuestionKey = null,
    IReadOnlyDictionary<string, string>? QuestionParams = null);

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

/// <param name="UserId">
/// Whose matters the drafts are checked against. Extraction alone does not need it;
/// deciding whether a draft is SETTLED does, because "clashes with something the
/// user already has" is one of the four things that makes it not.
/// </param>
/// <param name="Timezone">
/// The caller's IANA zone, as captured on the note. NOT validated on the way in —
/// <c>POST /me/voice-notes</c> stores any 1..64 character string verbatim, which is
/// the reference server's behaviour — so every consumer has to treat an
/// unrecognisable value as absent rather than trusting it.
/// </param>
/// <param name="SpokenAt">
/// When the user actually spoke, for resolving relative dates. The client sends
/// <c>x-voice-note-captured-at</c> as the recording's START (now − durationMs), and
/// that is the anchor the product means: a note dictated at 23:55 and picked up by
/// the worker at 00:02 must still read "tomorrow" as the day the speaker meant, not
/// the day the queue got to it. Null falls back to now.
/// </param>
public sealed record VoiceExtractionRequest(
    ObjectId UserId,
    string Transcript,
    string? Timezone,
    string? Locale,
    DateTime? SpokenAt = null);

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
