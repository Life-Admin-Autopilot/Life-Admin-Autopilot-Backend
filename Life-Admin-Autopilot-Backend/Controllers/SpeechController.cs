using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class SpeechController : ControllerBase
    {
        private readonly ISpeechToTextService _speechToTextService;

        public SpeechController(ISpeechToTextService speechToTextService)
        {
            _speechToTextService = speechToTextService;
        }

        // Audio in, transcript out. The Planning Agent path calls ISpeechToTextService
        // directly instead of coming through here - this endpoint exists so the mobile
        // client (and a manual test) can transcribe on its own.
        [HttpPost("transcribe")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(TranscriptionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(TranscriptionResponse), StatusCodes.Status400BadRequest)]
        // Nullable on purpose: a non-nullable IFormFile makes [ApiController]'s model
        // validation reject a missing file with its own ProblemDetails before this method
        // runs, which would contradict the documented ASR_NO_AUDIO contract.
        public async Task<IActionResult> Transcribe(IFormFile? audio, [FromForm] string? language, CancellationToken cancellationToken)
        {
            if (audio is null || audio.Length == 0)
            {
                return BadRequest(TranscriptionResponse.Fail(
                    SpeechErrorCodes.NoAudio,
                    "No audio file was uploaded."));
            }

            await using var stream = audio.OpenReadStream();

            var response = await _speechToTextService.TranscribeAsync(
                new AudioUpload(stream, audio.FileName, audio.ContentType, audio.Length),
                language,
                cancellationToken);

            if (response.Succeeded)
            {
                return Ok(response);
            }

            // A provider failure is reported as a gateway problem, not as a server crash
            // (NFR-8) - the client can tell the two apart and retry only where it helps.
            return StatusCode(MapStatusCode(response.ErrorCode!), response);
        }

        private static int MapStatusCode(string errorCode) => errorCode switch
        {
            _ when SpeechErrorCodes.IsClientError(errorCode) => StatusCodes.Status400BadRequest,
            SpeechErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
            SpeechErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
            // Both mean "correctly configured or not, this deployment cannot transcribe
            // right now" - an operator has to act, so retrying the request is pointless.
            SpeechErrorCodes.NotConfigured => StatusCodes.Status503ServiceUnavailable,
            SpeechErrorCodes.QuotaExceeded => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway
        };
    }
}
