using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.BLL.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.BLL
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddBusinessLogicLayer(this IServiceCollection services, IConfiguration configuration)
        {
            // The legacy IAuthService / IJwtTokenService pair was removed with
            // Controllers/AuthController.cs: it signed tokens from Jwt:Key and
            // authenticated with UserManager.CheckPasswordAsync, which neither
            // increments AccessFailedCount nor honours lockout. Every route it
            // served now lives under Features/Auth, behind the kernel's rate
            // limiters. See docs/KERNEL.md §13.
            services.AddScoped<INotificationService, NotificationService>();
            services.AddScoped<ISpeechToTextService, SpeechToTextService>();

            return services;
        }
    }
}