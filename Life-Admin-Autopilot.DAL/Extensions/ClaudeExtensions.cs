using Life_Admin_Autopilot.DAL.Claude;
using Life_Admin_Autopilot.DAL.Configurations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class ClaudeExtensions
    {
        public static IServiceCollection AddClaudeService(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<ClaudeOptions>()
                .Bind(configuration.GetSection(ClaudeOptions.SectionName))
                .PostConfigure(options =>
                {
                    // The gateway token comes from SBG_API_KEY (an env var in real
                    // deployments, user-secrets locally) - never from appsettings.json.
                    options.ApiKey = configuration["SBG_API_KEY"] ?? string.Empty;
                });

            var maxRetryAttempts = configuration.GetValue($"{ClaudeOptions.SectionName}:MaxRetryAttempts", 3);
            var timeoutSeconds = configuration.GetValue($"{ClaudeOptions.SectionName}:TimeoutSeconds", 30);

            services
                .AddHttpClient<IClaudeService, ClaudeService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                })
                .AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        maxRetryAttempts,
                        retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

            return services;
        }
    }
}