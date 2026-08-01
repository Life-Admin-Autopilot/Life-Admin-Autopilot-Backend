using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class SpeechExtensions
    {
        public static IServiceCollection AddSpeechServices(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<SpeechOptions>()
                .Bind(configuration.GetSection(SpeechOptions.SectionName))
                .PostConfigure(options =>
                {
                    // The Hugging Face token comes from HF_TOKEN (an env var in real
                    // deployments, user-secrets locally) - never from appsettings.json.
                    options.ApiKey = configuration["HF_TOKEN"] ?? string.Empty;
                });

            var maxRetryAttempts = configuration.GetValue($"{SpeechOptions.SectionName}:MaxRetryAttempts", 2);
            var timeoutSeconds = configuration.GetValue($"{SpeechOptions.SectionName}:TimeoutSeconds", 30);

            services
                .AddHttpClient<ITranscriptionService, NemotronTranscriptionService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                })
                // Linear, short backoff rather than the exponential one used for Claude and
                // FCM: a user is waiting on this call, so recovering slowly is no better
                // than failing fast and telling them to try again.
                .AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        maxRetryAttempts,
                        retryAttempt => TimeSpan.FromMilliseconds(250 * retryAttempt)));

            return services;
        }
    }
}
