using Life_Admin_Autopilot.DAL.Entities;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public class DeviceTokenRepository : IDeviceTokenRepository
    {
        private readonly IMongoCollection<DeviceToken> _deviceTokens;

        public DeviceTokenRepository(IMongoDatabase database)
        {
            _deviceTokens = database.GetCollection<DeviceToken>("deviceTokens");
        }

        public async Task<DeviceToken> UpsertAsync(DeviceToken deviceToken)
        {
            var now = DateTime.UtcNow;
            var existing = await GetByTokenAsync(deviceToken.Token);

            if (existing is null)
            {
                deviceToken.RegisteredAt = now;
                deviceToken.LastSeenAt = now;
                deviceToken.IsActive = true;
                deviceToken.DeactivatedAt = null;
                deviceToken.DeactivationReason = null;

                await _deviceTokens.InsertOneAsync(deviceToken);

                return deviceToken;
            }

            // A token can legitimately move between users on a shared device, so the
            // owner is re-stamped here rather than treated as a conflict.
            existing.UserId = deviceToken.UserId;
            existing.Platform = deviceToken.Platform;
            existing.DeviceModel = deviceToken.DeviceModel ?? existing.DeviceModel;
            existing.LastSeenAt = now;
            existing.IsActive = true;
            existing.DeactivatedAt = null;
            existing.DeactivationReason = null;

            await _deviceTokens.ReplaceOneAsync(
                token => token.Id == existing.Id,
                existing);

            return existing;
        }

        public async Task<DeviceToken?> GetByTokenAsync(string token)
        {
            return await _deviceTokens
                .Find(deviceToken => deviceToken.Token == token)
                .FirstOrDefaultAsync();
        }

        public async Task<List<DeviceToken>> GetActiveByUserIdAsync(string userId)
        {
            return await _deviceTokens
                .Find(deviceToken => deviceToken.UserId == userId && deviceToken.IsActive)
                .ToListAsync();
        }

        public async Task<bool> DeactivateAsync(string token, string reason)
        {
            var update = Builders<DeviceToken>.Update
                .Set(deviceToken => deviceToken.IsActive, false)
                .Set(deviceToken => deviceToken.DeactivatedAt, DateTime.UtcNow)
                .Set(deviceToken => deviceToken.DeactivationReason, reason);

            var result = await _deviceTokens.UpdateOneAsync(
                deviceToken => deviceToken.Token == token && deviceToken.IsActive,
                update);

            return result.ModifiedCount > 0;
        }

        public async Task<bool> DeleteAsync(string userId, string token)
        {
            var result = await _deviceTokens.DeleteOneAsync(
                deviceToken => deviceToken.Token == token && deviceToken.UserId == userId);

            return result.DeletedCount > 0;
        }
    }
}