using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Identity;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class AuthService : IAuthService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly IRefreshTokenRepository _refreshTokenRepository;

        public AuthService(
            UserManager<ApplicationUser> userManager,
            IJwtTokenService jwtTokenService,
            IRefreshTokenRepository refreshTokenRepository)
        {
            _userManager = userManager;
            _jwtTokenService = jwtTokenService;
            _refreshTokenRepository = refreshTokenRepository;
        }

        public async Task<AuthResult> RegisterAsync(RegisterRequest request)
        {
            var existing = await _userManager.FindByEmailAsync(request.Email);
            if (existing is not null)
                return AuthResult.Fail("A user with this email already exists.");

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                ProfilePictureUrl = request.ProfilePictureUrl
            };

            var createResult = await _userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
                return AuthResult.Fail(createResult.Errors.Select(e => e.Description));

            return await IssueTokensAsync(user);
        }

        public async Task<AuthResult> LoginAsync(LoginRequest request)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
                return AuthResult.Fail("Invalid email or password.");

            return await IssueTokensAsync(user);
        }

        public async Task<AuthResult> RefreshAsync(string refreshToken)
        {
            var existingToken = await _refreshTokenRepository.GetByTokenAsync(refreshToken);
            if (existingToken is null || !existingToken.IsActive)
                return AuthResult.Fail("Invalid or expired refresh token.");

            var user = await _userManager.FindByIdAsync(existingToken.UserId.ToString());
            if (user is null)
                return AuthResult.Fail("Invalid or expired refresh token.");

            await _refreshTokenRepository.RevokeAsync(existingToken);

            return await IssueTokensAsync(user);
        }

        public async Task<bool> LogoutAsync(string refreshToken)
        {
            var existingToken = await _refreshTokenRepository.GetByTokenAsync(refreshToken);
            if (existingToken is null || !existingToken.IsActive)
                return false;

            await _refreshTokenRepository.RevokeAsync(existingToken);
            return true;
        }

        private async Task<AuthResult> IssueTokensAsync(ApplicationUser user)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var (accessToken, expiresAt) = _jwtTokenService.GenerateAccessToken(user, roles);

            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Token = _jwtTokenService.GenerateRefreshTokenValue(),
                ExpiresAt = DateTime.UtcNow.AddDays(_jwtTokenService.RefreshTokenExpiryDays)
            };
            await _refreshTokenRepository.AddAsync(refreshToken);

            return AuthResult.Success(accessToken, refreshToken.Token, expiresAt);
        }
    }
}