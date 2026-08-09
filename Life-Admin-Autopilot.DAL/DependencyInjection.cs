using Life_Admin_Autopilot.DAL.Data;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Kernel.Data;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.DAL
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddDataAccessLayer(this IServiceCollection services, IConfiguration configuration)
        {
            // Engine chosen by `Database:Provider` (SqlServer | Sqlite), defaulting to
            // SqlServer so nothing changes unless configured. See DatabaseProvider —
            // in particular the note that SQL Server owns the canonical migrations and
            // SQLite must never generate one.
            services.AddDbContext<ApplicationDbContext>(options =>
                DatabaseProvider.ApplyTo(options, configuration));

            // No-op unless the provider is SQLite.
            services.AddHostedService<SqliteSchemaInitializer>();

            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.Password.RequiredLength = 8;
                    options.User.RequireUniqueEmail = true;
                })
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

            return services;
        }
    }
}