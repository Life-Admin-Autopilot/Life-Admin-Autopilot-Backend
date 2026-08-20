using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class PushNotificationExtensions
    {
        public static IServiceCollection AddPushNotifications(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<PushNotificationOptions>()
                .Bind(configuration.GetSection(PushNotificationOptions.SectionName))
                .PostConfigure(options =>
                {
                    // The service account is a private key - it comes from env vars in real
                    // deployments and user-secrets locally, never from appsettings.json.
                    //
                    // ONLY when actually supplied. These two assignments used to be
                    // unconditional, with `?? string.Empty` - and because PostConfigure runs
                    // AFTER Bind, that silently erased anything bound from the
                    // PushNotifications section. tools/dev/stack.sh configures push by
                    // exporting PushNotifications__ServiceAccountFilePath, so the documented
                    // way to switch delivery on was wiped a moment later, reported
                    // PUSH_NOT_CONFIGURED, and looked for all the world like a bad key.
                    var json = configuration["FCM_SERVICE_ACCOUNT_JSON"];
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        options.ServiceAccountJson = json;
                    }

                    var file = configuration["FCM_SERVICE_ACCOUNT_FILE"];
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        options.ServiceAccountFilePath = file;
                    }
                });

            // Singleton so the OAuth2 access token is minted once and reused until it
            // nears expiry, instead of on every notification.
            services.AddSingleton<IFcmAccessTokenProvider, FcmAccessTokenProvider>();

            services.AddScoped<IDeviceTokenRepository, DeviceTokenRepository>();

            var maxRetryAttempts = configuration.GetValue($"{PushNotificationOptions.SectionName}:MaxRetryAttempts", 3);
            var timeoutSeconds = configuration.GetValue($"{PushNotificationOptions.SectionName}:TimeoutSeconds", 30);

            services
                .AddHttpClient<IPushNotificationService, FcmPushNotificationService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                })
                // Only transient faults (5xx, 408, network) are retried. An invalid token
                // comes back as 404/400 and must not be retried - it will never succeed.
                .AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        maxRetryAttempts,
                        retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

            return services;
        }
    }
}
