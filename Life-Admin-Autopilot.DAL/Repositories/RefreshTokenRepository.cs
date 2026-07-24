using Life_Admin_Autopilot.DAL.Data;
using Life_Admin_Autopilot.DAL.Entities;
using Microsoft.EntityFrameworkCore;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public class RefreshTokenRepository : IRefreshTokenRepository
    {
        private readonly ApplicationDbContext _context;

        public RefreshTokenRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(RefreshToken token)
        {
            _context.RefreshTokens.Add(token);
            await _context.SaveChangesAsync();
        }

        public Task<RefreshToken?> GetByTokenAsync(string token) =>
            _context.RefreshTokens.FirstOrDefaultAsync(rt => rt.Token == token);

        public async Task RevokeAsync(RefreshToken token)
        {
            token.RevokedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
    }
}