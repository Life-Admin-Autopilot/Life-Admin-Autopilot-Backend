namespace Life_Admin_Autopilot.BLL.Dtos
{
    public record RegisterRequest(string Email, string Password, string? ProfilePictureUrl = null);

    public record LoginRequest(string Email, string Password);

    public record RefreshRequest(string RefreshToken);

    public record LogoutRequest(string RefreshToken);
}