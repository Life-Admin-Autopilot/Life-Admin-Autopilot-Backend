using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Storage.Models;

namespace Life_Admin_Autopilot.DAL.Storage
{
    // The only thing in the codebase allowed to talk to Azure Blob Storage directly.
    public interface IFileStorageService
    {
        // Lands a document in the staging container. Nothing is written to Mongo yet -
        // the file exists so Claude can extract from it and the user can preview it while
        // deciding (SRS 7.1). Abandoned staged blobs expire via a lifecycle rule.
        Task<Result<StoredFile>> UploadStagedDocumentAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default);

        // Called at commit time: moves a staged blob into the permanent container so the
        // saved document record points at something the cleanup rule will not delete.
        Task<Result<StoredFile>> PromoteStagedDocumentAsync(
            string stagedPath,
            CancellationToken cancellationToken = default);

        Task<Result<StoredFile>> UploadAvatarAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default);

        // For the Document Agent: pulls the bytes back so they can be handed to Claude.
        Task<Result<DownloadedFile>> DownloadAsync(
            string path,
            CancellationToken cancellationToken = default);

        // A short-lived read URL for the client. Generated per request rather than stored,
        // so nothing in the database ever holds a credential or goes stale.
        Result<string> CreateReadUrl(string path, string requestingUserId);

        Task<Result<bool>> DeleteAsync(string path, CancellationToken cancellationToken = default);
    }
}
