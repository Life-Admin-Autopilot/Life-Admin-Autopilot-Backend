using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    public class InMemoryDeviceTokenRepository : IDeviceTokenRepository
    {
        private readonly List<DeviceToken> _devices = new();

        public IReadOnlyList<DeviceToken> All => _devices;

        public Task<DeviceToken> UpsertAsync(DeviceToken deviceToken)
        {
            var now = DateTime.UtcNow;
            var existing = _devices.FirstOrDefault(device => device.Token == deviceToken.Token);

            if (existing is null)
            {
                deviceToken.Id = Guid.NewGuid().ToString();
                deviceToken.RegisteredAt = now;
                deviceToken.LastSeenAt = now;
                deviceToken.IsActive = true;
                _devices.Add(deviceToken);

                return Task.FromResult(deviceToken);
            }

            existing.UserId = deviceToken.UserId;
            existing.Platform = deviceToken.Platform;
            existing.DeviceModel = deviceToken.DeviceModel ?? existing.DeviceModel;
            existing.LastSeenAt = now;
            existing.IsActive = true;
            existing.DeactivatedAt = null;
            existing.DeactivationReason = null;

            return Task.FromResult(existing);
        }

        public Task<DeviceToken?> GetByTokenAsync(string token) =>
            Task.FromResult(_devices.FirstOrDefault(device => device.Token == token));

        public Task<List<DeviceToken>> GetActiveByUserIdAsync(string userId) =>
            Task.FromResult(_devices.Where(device => device.UserId == userId && device.IsActive).ToList());

        public Task<bool> DeactivateAsync(string token, string reason)
        {
            var device = _devices.FirstOrDefault(candidate => candidate.Token == token && candidate.IsActive);
            if (device is null)
            {
                return Task.FromResult(false);
            }

            device.IsActive = false;
            device.DeactivatedAt = DateTime.UtcNow;
            device.DeactivationReason = reason;

            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string userId, string token) =>
            Task.FromResult(_devices.RemoveAll(device => device.Token == token && device.UserId == userId) > 0);

        public void Seed(params DeviceToken[] devices) => _devices.AddRange(devices);
    }
}
