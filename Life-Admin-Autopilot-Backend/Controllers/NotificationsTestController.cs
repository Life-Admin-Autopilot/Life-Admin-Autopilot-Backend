using System.Security.Claims;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    // Manual verification surface for push delivery on real hardware. Authorized so it
    // cannot be used as an open relay to push arbitrary text at any device token.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class NotificationsTestController : ControllerBase
    {
        private readonly INotificationService _notificationService;

        public NotificationsTestController(INotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        // Mirrors what a reminder will do: fan out to every device the caller registered.
        [HttpPost("send-to-me")]
        public async Task<IActionResult> SendToMe(PushMessage message, CancellationToken cancellationToken)
        {
            var report = await _notificationService.SendToUserAsync(CurrentUserId, message, cancellationToken);

            if (!report.HasRegisteredDevices)
                return NotFound("No active device is registered for this user.");

            // A test that reached nobody must not look like a pass.
            if (report.SentCount == 0)
                return StatusCode(StatusCodes.Status502BadGateway, report);

            return Ok(report);
        }

        // Targets one device directly - the quickest way to prove a specific handset, or
        // to confirm that a known-bad token is reported rather than swallowed.
        [HttpPost("send-to-token")]
        public async Task<IActionResult> SendToToken(SendToTokenRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.DeviceToken))
                return BadRequest("A device token is required.");

            var result = await _notificationService.SendToTokenAsync(
                request.DeviceToken,
                new PushMessage(request.Title, request.Body),
                cancellationToken);

            if (!result.Succeeded)
                return StatusCode(StatusCodes.Status502BadGateway, result);

            return Ok(result);
        }

        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }
}
