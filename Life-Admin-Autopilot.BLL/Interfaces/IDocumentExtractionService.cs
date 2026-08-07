using Life_Admin_Autopilot.BLL.Dtos;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IDocumentExtractionService
    {
        // Reads a staged file and describes it. Ownership is checked against the caller,
        // so a path alone is not enough to read someone else's document (NFR-5).
        Task<DocumentExtractionResponse> ExtractAsync(
            string userId,
            string path,
            CancellationToken cancellationToken = default);
    }
}
