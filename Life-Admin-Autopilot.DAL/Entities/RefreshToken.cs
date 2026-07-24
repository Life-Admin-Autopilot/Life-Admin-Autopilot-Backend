namespace Life_Admin_Autopilot.DAL.Entities
{
    public class RefreshToken
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
        public ApplicationUser User { get; set; } = null!;

        public string Token { get; set; } = null!;
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }

        public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
    }
}