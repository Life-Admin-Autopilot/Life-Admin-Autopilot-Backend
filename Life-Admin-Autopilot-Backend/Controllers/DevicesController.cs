using System.Security.Claims;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    // Device tokens are always taken from the caller's own JWT, never from the request
    // body - otherwise anyone could attach a token to someone else's account and receive
    // their reminders.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DevicesController : ControllerBase
    {
        private readonly INotificationService _notificationService;

        public DevicesController(INotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest? request)
        {
            // A body that failed to bind arrives as null. Dereferencing it produced a
            // 500 for every malformed request - including, before DevicePlatform
            // learned to read its own name, every REAL request the app sent.
            if (request is null)
                return BadRequest("Send a JSON body with a token and a platform.");

            if (string.IsNullOrWhiteSpace(request.Token))
                return BadRequest("A device token is required.");

            if (request.Platform is not { } platform)
                return BadRequest("A platform is required: 'Ios' or 'Android'.");

            // Enum.IsDefined is what rejects a numeric value outside the enum. The
            // string converter accepts integers by default, so without this a
            // "platform": 42 would be stored and every later push to that row would
            // pick a provider that does not exist.
            if (!Enum.IsDefined(platform))
                return BadRequest("Platform must be 'Ios' or 'Android'.");

            var device = await _notificationService.RegisterDeviceAsync(
                CurrentUserId,
                request with { Platform = platform });

            return Ok(device);
        }

        [HttpGet]
        public async Task<IActionResult> GetMyDevices()
        {
            var devices = await _notificationService.GetDevicesAsync(CurrentUserId);

            return Ok(devices);
        }

        // Called on logout, so a shared or handed-down device stops receiving the previous
        // owner's reminders.
        [HttpDelete]
        public async Task<IActionResult> Unregister(UnregisterDeviceRequest request)
        {
            var removed = await _notificationService.UnregisterDeviceAsync(CurrentUserId, request.Token);

            if (!removed)
                return NotFound("No such device is registered for this user.");

            return NoContent();
        }

        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }
}
