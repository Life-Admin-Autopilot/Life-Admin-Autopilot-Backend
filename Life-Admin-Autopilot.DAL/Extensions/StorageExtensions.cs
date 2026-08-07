using Azure.Storage.Blobs;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class StorageExtensions
    {
        public static IServiceCollection AddFileStorage(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<StorageOptions>()
                .Bind(configuration.GetSection(StorageOptions.SectionName))
                .PostConfigure(options =>
                {
                    // A full-access account key - env var in real deployments,
                    // user-secrets locally, never appsettings.json.
                    options.ConnectionString = configuration["AZURE_STORAGE_CONNECTION_STRING"] ?? string.Empty;
                });

            // Always registered, even with no connection string: the API still starts and
            // every other feature works, while storage calls report why they cannot run
            // rather than taking the host down at boot.
            services.AddSingleton(_ =>
                BlobClientProvider.FromConnectionString(configuration["AZURE_STORAGE_CONNECTION_STRING"]));

            services.AddScoped<IFileStorageService, AzureBlobStorageService>();

            return services;
        }
    }
}
