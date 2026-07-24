using Microsoft.AspNetCore.Identity;

namespace Life_Admin_Autopilot.DAL.Entities
{
    public class ApplicationUser : IdentityUser<Guid>
    {
        public string? ProfilePictureUrl { get; set; }
    }
}