using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IJwtTokenService
    {
        int RefreshTokenExpiryDays { get; }

        (string Token, DateTime ExpiresAt) GenerateAccessToken(ApplicationUser user, IEnumerable<string> roles);

        string GenerateRefreshTokenValue();
    }
}