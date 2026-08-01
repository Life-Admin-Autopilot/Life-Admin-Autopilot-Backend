using Life_Admin_Autopilot.BLL;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Extensions;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.Tests.Push
{
    // The push feature spans three registration calls across two layers, so the container
    // is exercised here rather than discovered to be misconfigured at the first reminder.
    public class PushNotificationWiringTests
    {
        [Fact]
        public void NotificationServiceAndItsDependenciesResolveFromTheContainer()
        {
            using var provider = BuildProvider();
            using var scope = provider.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetRequiredService<INotificationService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPushNotificationService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeviceTokenRepository>());
        }

        // No credential configured is a supported state: the API still starts, and sends
        // report PUSH_NOT_CONFIGURED instead of throwing.
        [Fact]
        public async Task AccessTokenProviderReportsMissingCredentialsInsteadOfThrowing()
        {
            using var provider = BuildProvider();

            var accessTokenProvider = provider.GetRequiredService<IFcmAccessTokenProvider>();
            var result = await accessTokenProvider.GetAccessTokenAsync();

            Assert.False(accessTokenProvider.IsConfigured);
            Assert.True(result.IsFailure);
            Assert.Equal(DAL.Push.Models.PushErrorCodes.NotConfigured, result.Error!.Code);
        }

        private static ServiceProvider BuildProvider()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // A connection string is enough: the Mongo driver connects lazily, so
                    // no server is needed to prove the wiring.
                    ["MongoDbSettings:ConnectionString"] = "mongodb://localhost:27017",
                    ["MongoDbSettings:DatabaseName"] = "LifeAdminAutopilotTest"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMongoDb(configuration);
            services.AddPushNotifications(configuration);
            services.AddBusinessLogicLayer(configuration);

            return services.BuildServiceProvider(validateScopes: true);
        }
    }
}
