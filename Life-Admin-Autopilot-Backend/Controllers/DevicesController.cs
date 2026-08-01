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
        public async Task<IActionResult> Register(RegisterDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Token))
                return BadRequest("A device token is required.");

            var device = await _notificationService.RegisterDeviceAsync(CurrentUserId, request);

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
