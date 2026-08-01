using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    // Deliberately carries a masked token: enough to recognise a device in the list,
    // not enough to push to it.
    public record RegisteredDeviceResponse(
        string DeviceToken,
        DevicePlatform Platform,
        string? DeviceModel,
        DateTime RegisteredAt,
        DateTime LastSeenAt);
}
