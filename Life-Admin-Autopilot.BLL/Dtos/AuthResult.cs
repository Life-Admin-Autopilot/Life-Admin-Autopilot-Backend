namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class AuthResult
    {
        public bool Succeeded { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public DateTime? AccessTokenExpiresAt { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        public static AuthResult Success(string accessToken, string refreshToken, DateTime accessTokenExpiresAt) => new()
        {
            Succeeded = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            AccessTokenExpiresAt = accessTokenExpiresAt
        };

        public static AuthResult Fail(params string[] errors) => new()
        {
            Succeeded = false,
            Errors = errors
        };

        public static AuthResult Fail(IEnumerable<string> errors) => new()
        {
            Succeeded = false,
            Errors = errors.ToList()
        };
    }
}