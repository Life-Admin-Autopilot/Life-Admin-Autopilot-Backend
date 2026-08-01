using Life_Admin_Autopilot.BLL.Dtos;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface INotificationService
    {
        Task<RegisteredDeviceResponse> RegisterDeviceAsync(string userId, RegisterDeviceRequest request);

        Task<bool> UnregisterDeviceAsync(string userId, string token);

        Task<IReadOnlyList<RegisteredDeviceResponse>> GetDevicesAsync(string userId);

        // Fans out to every active device the user has registered. Reminders call this;
        // they should not have to know how many phones a user owns.
        Task<PushDeliveryReport> SendToUserAsync(
            string userId,
            PushMessage message,
            CancellationToken cancellationToken = default);

        // Single-token send, for verifying a specific physical device end to end.
        Task<PushDeliveryResult> SendToTokenAsync(
            string deviceToken,
            PushMessage message,
            CancellationToken cancellationToken = default);
    }
}
