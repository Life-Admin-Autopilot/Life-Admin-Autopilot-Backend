using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.DAL.Storage.Models;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IFileUploadService
    {
        // Stages a document for /planning/propose: the file lands in blob storage so Claude
        // can extract from it and the user can preview it, but nothing is written to Mongo
        // until the proposal is confirmed.
        Task<FileUploadResponse> StageDocumentAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default);

        // Uploads a profile picture and points the user's profile at it (FR-1.6).
        Task<FileUploadResponse> UploadAvatarAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default);

        // Mints a short-lived URL for a file the caller owns. Ownership is enforced.
        FileUploadResponse CreateReadUrl(string userId, string path);
    }
}
