using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IDeviceTokenRepository
    {
        // Registration is idempotent: the client re-registers on every app start and after
        // every FCM token rotation, so an existing token is refreshed rather than duplicated.
        Task<DeviceToken> UpsertAsync(DeviceToken deviceToken);

        Task<DeviceToken?> GetByTokenAsync(string token);

        Task<List<DeviceToken>> GetActiveByUserIdAsync(string userId);

        Task<bool> DeactivateAsync(string token, string reason);

        Task<bool> DeleteAsync(string userId, string token);
    }
}