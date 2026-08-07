using System.Security.Claims;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Storage.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    // Uploads are always attributed to the caller's own id from the JWT, and read URLs are
    // only issued for files that id owns (NFR-5).
    [ApiController]
    [Route("api")]
    [Authorize]
    public class FilesController : ControllerBase
    {
        private readonly IFileUploadService _fileUploadService;

        public FilesController(IFileUploadService fileUploadService)
        {
            _fileUploadService = fileUploadService;
        }

        // Step one of the document-to-task chain: the file is stored so Claude can read it
        // and the user can preview it, before any task or document record exists. The
        // returned path is what /planning/commit will promote once the user confirms.
        [HttpPost("documents/staging")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> StageDocument(IFormFile? file, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest(FileUploadResponse.Fail(StorageErrorCodes.NoFile, "No document was uploaded."));
            }

            await using var stream = file.OpenReadStream();

            var response = await _fileUploadService.StageDocumentAsync(
                CurrentUserId,
                ToUpload(file, stream),
                cancellationToken);

            return Respond(response);
        }

        // FR-1.6: profile picture, stored in blob storage with the resulting path saved to
        // the user's profile.
        [HttpPost("users/me/avatar")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> UploadAvatar(IFormFile? file, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest(FileUploadResponse.Fail(StorageErrorCodes.NoFile, "No image was uploaded."));
            }

            await using var stream = file.OpenReadStream();

            var response = await _fileUploadService.UploadAvatarAsync(
                CurrentUserId,
                ToUpload(file, stream),
                cancellationToken);

            return Respond(response);
        }

        // Stored paths are not fetchable on their own - the client exchanges one for a
        // short-lived URL when it actually needs to display the file.
        [HttpGet("files/read-url")]
        [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status403Forbidden)]
        public IActionResult GetReadUrl([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(FileUploadResponse.Fail(StorageErrorCodes.NoFile, "A file path is required."));
            }

            return Respond(_fileUploadService.CreateReadUrl(CurrentUserId, path));
        }

        private IActionResult Respond(FileUploadResponse response)
        {
            if (response.Succeeded)
            {
                return Ok(response);
            }

            return StatusCode(MapStatusCode(response.ErrorCode!), response);
        }

        private static int MapStatusCode(string errorCode) => errorCode switch
        {
            StorageErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
            StorageErrorCodes.NotFound => StatusCodes.Status404NotFound,
            _ when StorageErrorCodes.IsClientError(errorCode) => StatusCodes.Status400BadRequest,
            StorageErrorCodes.NotConfigured => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway
        };

        private static FileUpload ToUpload(IFormFile file, Stream stream) => new()
        {
            Content = stream,
            FileName = file.FileName,
            ContentType = file.ContentType,
            LengthBytes = file.Length
        };

        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }
}
