using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    // Sent by the Capacitor client from the PushNotifications 'registration' listener on
    // every app start - FCM rotates tokens, so re-registration is normal, not an error.
    public record RegisterDeviceRequest(string Token, DevicePlatform Platform, string? DeviceModel = null);

    public record UnregisterDeviceRequest(string Token);

    // Data values must be strings: FCM rejects any other JSON type in the data payload.
    public record PushMessage(string Title, string Body, Dictionary<string, string>? Data = null);

    public record SendToTokenRequest(string DeviceToken, string Title, string Body);
}
