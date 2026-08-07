using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Storage.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Storage
{
    public class AzureBlobStorageService : IFileStorageService
    {
        private readonly BlobServiceClient? _blobServiceClient;
        private readonly StorageOptions _options;
        private readonly ILogger<AzureBlobStorageService> _logger;

        public AzureBlobStorageService(
            BlobClientProvider blobClientProvider,
            IOptions<StorageOptions> options,
            ILogger<AzureBlobStorageService> logger)
        {
            _blobServiceClient = blobClientProvider.Client;
            _options = options.Value;
            _logger = logger;
        }

        public Task<Result<StoredFile>> UploadStagedDocumentAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default) =>
            UploadAsync(_options.StagingContainer, userId, file, cancellationToken);

        public Task<Result<StoredFile>> UploadAvatarAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default) =>
            UploadAsync(_options.AvatarsContainer, userId, file, cancellationToken);

        private async Task<Result<StoredFile>> UploadAsync(
            string container,
            string userId,
            FileUpload file,
            CancellationToken cancellationToken)
        {
            if (_blobServiceClient is null)
            {
                return Fail<StoredFile>(StorageErrorCodes.NotConfigured, "No storage connection string is configured.");
            }

            if (file.LengthBytes <= 0)
            {
                return Fail<StoredFile>(StorageErrorCodes.NoFile, "The uploaded file is empty.");
            }

            var blobName = BlobPath.Create(userId, file.FileName);

            try
            {
                var blobClient = _blobServiceClient
                    .GetBlobContainerClient(container)
                    .GetBlobClient(blobName);

                await blobClient.UploadAsync(
                    file.Content,
                    new BlobUploadOptions
                    {
                        // Set explicitly so a later download and the browser both see the
                        // real type - Azure defaults to application/octet-stream.
                        HttpHeaders = new BlobHttpHeaders { ContentType = file.ContentType }
                    },
                    cancellationToken);

                var path = BlobPath.Combine(container, blobName);

                _logger.LogInformation(
                    "Stored {SizeBytes} bytes at {Path} for user {UserId}",
                    file.LengthBytes,
                    path,
                    userId);

                return Result<StoredFile>.Success(new StoredFile
                {
                    Path = path,
                    ContentType = file.ContentType,
                    SizeBytes = file.LengthBytes,
                    OriginalFileName = file.FileName
                });
            }
            catch (RequestFailedException ex)
            {
                return Fail<StoredFile>(MapErrorCode(ex.Status), Describe(ex));
            }
        }

        public async Task<Result<StoredFile>> PromoteStagedDocumentAsync(
            string stagedPath,
            CancellationToken cancellationToken = default)
        {
            if (_blobServiceClient is null)
            {
                return Fail<StoredFile>(StorageErrorCodes.NotConfigured, "No storage connection string is configured.");
            }

            if (!BlobPath.TrySplit(stagedPath, out var container, out var blobName))
            {
                return Fail<StoredFile>(StorageErrorCodes.NotFound, $"'{stagedPath}' is not a valid blob path.");
            }

            // Promoting anything other than a staged blob would mean the caller has its
            // wires crossed; silently copying it would hide the bug.
            if (container != _options.StagingContainer)
            {
                return Fail<StoredFile>(
                    StorageErrorCodes.AccessDenied,
                    $"Only blobs in '{_options.StagingContainer}' can be promoted; got '{container}'.");
            }

            try
            {
                var source = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
                var destination = _blobServiceClient
                    .GetBlobContainerClient(_options.DocumentsContainer)
                    .GetBlobClient(blobName);

                if (!await source.ExistsAsync(cancellationToken))
                {
                    // Most likely the lifecycle rule reclaimed it: the user left the
                    // confirmation screen open for more than a day.
                    return Fail<StoredFile>(
                        StorageErrorCodes.NotFound,
                        $"The staged file '{stagedPath}' no longer exists. It may have expired before being confirmed.");
                }

                var copy = await destination.StartCopyFromUriAsync(source.Uri, cancellationToken: cancellationToken);
                await copy.WaitForCompletionAsync(cancellationToken);

                // Only after the copy is durable - losing the source before the copy lands
                // would lose the user's document outright.
                await source.DeleteIfExistsAsync(cancellationToken: cancellationToken);

                var properties = await destination.GetPropertiesAsync(cancellationToken: cancellationToken);
                var path = BlobPath.Combine(_options.DocumentsContainer, blobName);

                _logger.LogInformation("Promoted {StagedPath} to {Path}", stagedPath, path);

                return Result<StoredFile>.Success(new StoredFile
                {
                    Path = path,
                    ContentType = properties.Value.ContentType ?? string.Empty,
                    SizeBytes = properties.Value.ContentLength,
                    OriginalFileName = blobName
                });
            }
            catch (RequestFailedException ex)
            {
                return Fail<StoredFile>(MapErrorCode(ex.Status), Describe(ex));
            }
        }

        public async Task<Result<DownloadedFile>> DownloadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            if (_blobServiceClient is null)
            {
                return Fail<DownloadedFile>(StorageErrorCodes.NotConfigured, "No storage connection string is configured.");
            }

            if (!BlobPath.TrySplit(path, out var container, out var blobName))
            {
                return Fail<DownloadedFile>(StorageErrorCodes.NotFound, $"'{path}' is not a valid blob path.");
            }

            try
            {
                var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
                var response = await blobClient.DownloadContentAsync(cancellationToken);

                return Result<DownloadedFile>.Success(new DownloadedFile
                {
                    Content = response.Value.Content.ToArray(),
                    ContentType = response.Value.Details.ContentType ?? string.Empty
                });
            }
            catch (RequestFailedException ex)
            {
                return Fail<DownloadedFile>(MapErrorCode(ex.Status), Describe(ex));
            }
        }

        public Result<string> CreateReadUrl(string path, string requestingUserId)
        {
            if (_blobServiceClient is null)
            {
                return Fail<string>(StorageErrorCodes.NotConfigured, "No storage connection string is configured.");
            }

            if (!BlobPath.TrySplit(path, out var container, out var blobName))
            {
                return Fail<string>(StorageErrorCodes.NotFound, $"'{path}' is not a valid blob path.");
            }

            // NFR-5, enforced from the path itself so a guessed document id cannot hand
            // one user a link to another user's passport scan.
            if (!BlobPath.IsOwnedBy(blobName, requestingUserId))
            {
                return Fail<string>(
                    StorageErrorCodes.AccessDenied,
                    $"User {requestingUserId} does not own '{path}'.");
            }

            var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobName);

            if (!blobClient.CanGenerateSasUri)
            {
                return Fail<string>(
                    StorageErrorCodes.NotConfigured,
                    "The storage client cannot mint SAS URLs; it was not created with an account key.");
            }

            var readUrl = blobClient.GenerateSasUri(
                BlobSasPermissions.Read,
                DateTimeOffset.UtcNow.AddMinutes(_options.ReadUrlLifetimeMinutes));

            return Result<string>.Success(readUrl.ToString());
        }

        public async Task<Result<bool>> DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            if (_blobServiceClient is null)
            {
                return Fail<bool>(StorageErrorCodes.NotConfigured, "No storage connection string is configured.");
            }

            if (!BlobPath.TrySplit(path, out var container, out var blobName))
            {
                return Fail<bool>(StorageErrorCodes.NotFound, $"'{path}' is not a valid blob path.");
            }

            try
            {
                var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobName);
                var deleted = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

                return Result<bool>.Success(deleted.Value);
            }
            catch (RequestFailedException ex)
            {
                return Fail<bool>(MapErrorCode(ex.Status), Describe(ex));
            }
        }

        // Every failure is logged here, where Azure's own status and message are intact.
        private Result<T> Fail<T>(string code, string message)
        {
            _logger.LogWarning("Blob storage operation failed: {ErrorCode} - {ErrorMessage}", code, message);

            return Result<T>.Failure(new Error(code, message));
        }

        private static string MapErrorCode(int status) => status switch
        {
            404 => StorageErrorCodes.NotFound,
            401 => StorageErrorCodes.NotAuthorized,
            403 => StorageErrorCodes.NotAuthorized,
            413 => StorageErrorCodes.FileTooLarge,
            >= 500 => StorageErrorCodes.Unavailable,
            _ => StorageErrorCodes.Unavailable
        };

        private static string Describe(RequestFailedException ex) =>
            $"Azure returned {ex.Status} ({ex.ErrorCode}): {ex.Message}";
    }
}
