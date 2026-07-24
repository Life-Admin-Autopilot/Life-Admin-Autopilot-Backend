using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterRequest request)
        {
            var result = await _authService.RegisterAsync(request);
            if (!result.Succeeded)
                return BadRequest(result.Errors);

            return Ok(new { result.AccessToken, result.RefreshToken, result.AccessTokenExpiresAt });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginRequest request)
        {
            var result = await _authService.LoginAsync(request);
            if (!result.Succeeded)
                return Unauthorized(result.Errors);

            return Ok(new { result.AccessToken, result.RefreshToken, result.AccessTokenExpiresAt });
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(RefreshRequest request)
        {
            var result = await _authService.RefreshAsync(request.RefreshToken);
            if (!result.Succeeded)
                return Unauthorized(result.Errors);

            return Ok(new { result.AccessToken, result.RefreshToken, result.AccessTokenExpiresAt });
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout(LogoutRequest request)
        {
            var revoked = await _authService.LogoutAsync(request.RefreshToken);
            if (!revoked)
                return NotFound();

            return NoContent();
        }
    }
}