using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IRefreshTokenRepository
    {
        Task AddAsync(RefreshToken token);
        Task<RefreshToken?> GetByTokenAsync(string token);
        Task RevokeAsync(RefreshToken token);
    }
}