using Life_Admin_Autopilot.BLL.Dtos;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IAuthService
    {
        Task<AuthResult> RegisterAsync(RegisterRequest request);
        Task<AuthResult> LoginAsync(LoginRequest request);
        Task<AuthResult> RefreshAsync(string refreshToken);
        Task<bool> LogoutAsync(string refreshToken);
    }
}