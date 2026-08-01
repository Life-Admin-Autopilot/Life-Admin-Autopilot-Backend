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
    }
}
