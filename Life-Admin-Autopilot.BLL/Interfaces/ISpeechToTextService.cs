using Life_Admin_Autopilot.BLL.Dtos;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    // This is the seam the rest of the backend transcribes through - including the
    // Planning Agent's /api/planning/propose path, which calls it in-process rather than
    // making a second HTTP hop to /api/speech/transcribe.
    public interface ISpeechToTextService
    {
        // Never throws for a provider failure, a timeout, or bad audio: the outcome is
        // always in the returned response so the caller can tell the user what happened.
        Task<TranscriptionResponse> TranscribeAsync(
            AudioUpload audio,
            string? language = null,
            CancellationToken cancellationToken = default);

        // The same provider policy - auto-detect first, one pinned retry for silence or
        // for Arabic that came back romanised - for audio that is ALREADY accepted.
        //
        // The voice-note worker reads bytes back out of storage. Those bytes passed a
        // different boundary on the way in: POST /me/voice-notes has its own size ceiling
        // and its own content-type list, and re-running them through the upload gate here
        // would reject, at transcription time, a recording the API already took and
        // acknowledged with a 202. The user would have no way to act on the rejection -
        // the recording is long gone.
        //
        // Existing to avoid the alternative, which was a second copy of the detect/pin/
        // repair chain living in the voice slice. Those two copies would drift, and the
        // drift would show up as one surface transcribing Egyptian Arabic correctly and
        // the other not.
        Task<TranscriptionResponse> TranscribeStoredAsync(
            byte[] audio,
            string contentType,
            string? language = null,
            CancellationToken cancellationToken = default);
    }
}
