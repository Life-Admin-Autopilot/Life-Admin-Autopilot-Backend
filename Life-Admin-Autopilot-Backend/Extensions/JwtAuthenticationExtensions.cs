using System.Text;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Life_Admin_Autopilot_Backend.Extensions
{
    public static class JwtAuthenticationExtensions
    {
        /// <summary>
        /// Registers the framework's own "Bearer" scheme.
        ///
        /// <para><b>Nothing currently selects it.</b> <c>AddKernel</c> runs after this
        /// and its <c>AddAuthentication(KernelBearer)</c> wins the default, so every
        /// bare <c>[Authorize]</c> resolves to the kernel handler — which is the
        /// parity-bound one. This registration is kept only so a scheme named
        /// "Bearer" still exists for anything that asks for it by name; removing it
        /// touches <c>Program.CreateApp</c>, which slices must not edit.</para>
        ///
        /// <para>It used to build its key from <c>Jwt:Key</c> alone with a
        /// null-forgiving <c>!</c>. That diverged from the kernel's three-key chain:
        /// a deployment setting only <c>Kernel:Jwt:AccessSecret</c> left this scheme
        /// validating against the placeholder in <c>appsettings.json</c>, so any
        /// route that did name it would have accepted forged tokens. It now reads the
        /// same resolved secret as everything else, and <c>UseKernel</c> has already
        /// refused to start if that secret is unusable.</para>
        /// </summary>
        public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
        {
            var jwtSection = configuration.GetSection("Jwt");

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    // Resolved inside the callback: options are built after
                    // configuration is final, so this sees environment overrides
                    // and the test host's in-memory settings alike.
                    var secret = KernelJwtSecret.Resolve(configuration);

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtSection["Issuer"],
                        ValidAudience = jwtSection["Audience"],
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                        ClockSkew = TimeSpan.Zero
                    };
                });

            return services;
        }
    }
}