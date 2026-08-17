using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    // Sent by the Capacitor client from the PushNotifications 'registration' listener on
    // every app start - FCM rotates tokens, so re-registration is normal, not an error.
    //
    // Token and Platform are NULLABLE so a body that omits them is distinguishable
    // from one that supplies them. Platform in particular: as a non-nullable enum a
    // missing value bound to default(DevicePlatform) — Android — so a client that
    // forgot the field silently registered every iPhone as an Android device, and
    // every push to it would have gone to the wrong provider. The controller turns
    // either absence into a 400.
    public record RegisterDeviceRequest(
        string? Token,
        DevicePlatform? Platform,
        string? DeviceModel = null);

    public record UnregisterDeviceRequest(string Token);

    // Data values must be strings: FCM rejects any other JSON type in the data payload.
    public record PushMessage(string Title, string Body, Dictionary<string, string>? Data = null);

    public record SendToTokenRequest(string DeviceToken, string Title, string Body);
}
