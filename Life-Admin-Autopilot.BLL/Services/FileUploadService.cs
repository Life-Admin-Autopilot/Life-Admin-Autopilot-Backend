using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Storage;
using Life_Admin_Autopilot.DAL.Storage.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class FileUploadService : IFileUploadService
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly StorageOptions _options;
        private readonly ILogger<FileUploadService> _logger;

        public FileUploadService(
            IFileStorageService fileStorageService,
            UserManager<ApplicationUser> userManager,
            IOptions<StorageOptions> options,
            ILogger<FileUploadService> logger)
        {
            _fileStorageService = fileStorageService;
            _userManager = userManager;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<FileUploadResponse> StageDocumentAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default)
        {
            var rejection = Validate(file, _options.MaxDocumentBytes, _options.AllowedDocumentContentTypes, "document");
            if (rejection is not null)
            {
                return Reject(userId, file, rejection);
            }

            var result = await _fileStorageService.UploadStagedDocumentAsync(userId, file, cancellationToken);
            if (result.IsFailure)
            {
                return Failed(userId, file, result.Error!.Code, result.Error.Message);
            }

            var stored = result.Value!;

            return FileUploadResponse.Success(
                stored.Path,
                // Handed back immediately so the confirmation screen can show the document
                // without a second round trip.
                _fileStorageService.CreateReadUrl(stored.Path, userId).Value,
                stored.ContentType,
                stored.SizeBytes);
        }

        public async Task<FileUploadResponse> UploadAvatarAsync(
            string userId,
            FileUpload file,
            CancellationToken cancellationToken = default)
        {
            var rejection = Validate(file, _options.MaxAvatarBytes, _options.AllowedAvatarContentTypes, "profile picture");
            if (rejection is not null)
            {
                return Reject(userId, file, rejection);
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Failed(userId, file, StorageErrorCodes.AccessDenied, "No such user.");
            }

            var result = await _fileStorageService.UploadAvatarAsync(userId, file, cancellationToken);
            if (result.IsFailure)
            {
                return Failed(userId, file, result.Error!.Code, result.Error.Message);
            }

            var stored = result.Value!;
            var previous = user.ProfilePictureUrl;

            user.ProfilePictureUrl = stored.Path;
            await _userManager.UpdateAsync(user);

            // The old avatar is now unreferenced, so it is removed rather than left to
            // accumulate - nothing else points at it and there is no lifecycle rule on
            // this container.
            if (!string.IsNullOrWhiteSpace(previous) && previous != stored.Path)
            {
                var deleted = await _fileStorageService.DeleteAsync(previous, cancellationToken);
                if (deleted.IsFailure)
                {
                    // Not worth failing the upload the user just made - the new avatar is
                    // saved and correct; this only leaves an orphaned blob behind.
                    _logger.LogWarning(
                        "Could not delete the previous avatar at {Path}: {ErrorCode} - {ErrorMessage}",
                        previous,
                        deleted.Error!.Code,
                        deleted.Error.Message);
                }
            }

            return FileUploadResponse.Success(
                stored.Path,
                _fileStorageService.CreateReadUrl(stored.Path, userId).Value,
                stored.ContentType,
                stored.SizeBytes);
        }

        public FileUploadResponse CreateReadUrl(string userId, string path)
        {
            var result = _fileStorageService.CreateReadUrl(path, userId);

            if (result.IsFailure)
            {
                _logger.LogWarning(
                    "Refused a read URL for {Path} to user {UserId}: {ErrorCode} - {ErrorMessage}",
                    path,
                    userId,
                    result.Error!.Code,
                    result.Error.Message);

                return FileUploadResponse.Fail(result.Error.Code, ToUserMessage(result.Error.Code, result.Error.Message));
            }

            return FileUploadResponse.Success(path, result.Value, string.Empty, 0);
        }

        // Caught before the upload so junk never costs a storage round trip.
        private static FileUploadResponse? Validate(
            FileUpload file,
            long maxBytes,
            List<string> allowedContentTypes,
            string label)
        {
            if (file.LengthBytes <= 0)
            {
                return FileUploadResponse.Fail(StorageErrorCodes.NoFile, $"No {label} was uploaded.");
            }

            if (file.LengthBytes > maxBytes)
            {
                return FileUploadResponse.Fail(
                    StorageErrorCodes.FileTooLarge,
                    $"The {label} is larger than the {maxBytes / (1024 * 1024)} MB limit.");
            }

            var contentType = NormalizeContentType(file.ContentType);
            if (!allowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            {
                return FileUploadResponse.Fail(
                    StorageErrorCodes.UnsupportedFormat,
                    $"'{contentType}' is not a supported {label} format. Allowed: {string.Join(", ", allowedContentTypes)}.");
            }

            return null;
        }

        // Browsers append parameters (image/jpeg;charset=utf-8) that would break an
        // exact-match check against the allow list.
        private static string NormalizeContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return string.Empty;
            }

            var separatorIndex = contentType.IndexOf(';');

            return (separatorIndex < 0 ? contentType : contentType[..separatorIndex]).Trim();
        }

        private FileUploadResponse Reject(string userId, FileUpload file, FileUploadResponse rejection)
        {
            _logger.LogWarning(
                "Rejected upload '{FileName}' ({ContentType}, {LengthBytes} bytes) from user {UserId}: {ErrorCode}",
                file.FileName,
                file.ContentType,
                file.LengthBytes,
                userId,
                rejection.ErrorCode);

            return rejection;
        }

        private FileUploadResponse Failed(string userId, FileUpload file, string errorCode, string errorMessage)
        {
            _logger.LogWarning(
                "Upload of '{FileName}' for user {UserId} failed: {ErrorCode} - {ErrorMessage}",
                file.FileName,
                userId,
                errorCode,
                errorMessage);

            return FileUploadResponse.Fail(errorCode, ToUserMessage(errorCode, errorMessage));
        }

        // Azure messages carry request ids and internal detail, so anything caused by our
        // side of the integration is replaced with something a user can act on.
        private static string ToUserMessage(string errorCode, string providerMessage) => errorCode switch
        {
            StorageErrorCodes.NotFound => "That file no longer exists.",
            StorageErrorCodes.AccessDenied => "You do not have access to that file.",
            StorageErrorCodes.NotAuthorized => "File storage is not available right now.",
            StorageErrorCodes.NotConfigured => "File storage is not available right now.",
            StorageErrorCodes.Unavailable => "File storage is temporarily unavailable. Please try again shortly.",
            _ => providerMessage
        };
    }
}
