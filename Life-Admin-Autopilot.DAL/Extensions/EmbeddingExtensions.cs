using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Embeddings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class EmbeddingExtensions
    {
        public static IServiceCollection AddEmbeddings(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<EmbeddingOptions>()
                .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
                .PostConfigure(options =>
                {
                    // HF_TOKEN: env var in deployments, user-secrets locally.
                    options.ApiKey = configuration["HF_TOKEN"] ?? string.Empty;
                });

            var maxRetryAttempts = configuration.GetValue($"{EmbeddingOptions.SectionName}:MaxRetryAttempts", 2);
            var timeoutSeconds = configuration.GetValue($"{EmbeddingOptions.SectionName}:TimeoutSeconds", 60);
            var baseUrl = configuration[$"{EmbeddingOptions.SectionName}:BaseUrl"]
                ?? "https://router.huggingface.co";

            services
                .AddHttpClient<IEmbeddingService, HuggingFaceEmbeddingService>(client =>
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                })
                .AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        maxRetryAttempts,
                        // A cold model on the free tier answers 503 for a few seconds,
                        // so backing off is usually enough to get a vector.
                        retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

            return services;
        }
    }
}
