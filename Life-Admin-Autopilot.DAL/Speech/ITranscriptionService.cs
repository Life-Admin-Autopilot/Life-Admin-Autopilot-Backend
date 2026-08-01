using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Speech.Models;

namespace Life_Admin_Autopilot.DAL.Speech
{
    // The only thing in the codebase allowed to call the ASR provider directly. Feature
    // code goes through the BLL speech-to-text service, which owns validation and the
    // user-facing error mapping.
    public interface ITranscriptionService
    {
        Task<Result<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default);
    }
}
