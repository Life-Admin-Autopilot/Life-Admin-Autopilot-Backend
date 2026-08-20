using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Extensions;
using Life_Admin_Autopilot.DAL.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Push
{
    /// <summary>
    /// How the FCM credential is SUPPLIED, as opposed to how it is used.
    ///
    /// <para>
    /// Both of these were live defects. PostConfigure assigned
    /// <c>configuration["FCM_SERVICE_ACCOUNT_FILE"] ?? string.Empty</c> unconditionally
    /// and runs after Bind, so it erased anything the <c>PushNotifications</c> section
    /// had provided — which is exactly how tools/dev/stack.sh turns push on. The
    /// result was a deployment that had been configured correctly by the documented
    /// route and still reported PUSH_NOT_CONFIGURED.
    /// </para>
    /// </summary>
    public class PushCredentialLoadTests
    {
        private static PushNotificationOptions Resolve(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            return new ServiceCollection()
                .AddLogging()
                .AddPushNotifications(configuration)
                .BuildServiceProvider()
                .GetRequiredService<IOptions<PushNotificationOptions>>()
                .Value;
        }

        [Fact]
        public void TheBoundSectionSurvives_WhenNoEnvironmentOverrideIsSupplied()
        {
            var options = Resolve(new()
            {
                ["PushNotifications:ServiceAccountFilePath"] = "/keys/sa.json",
            });

            Assert.Equal("/keys/sa.json", options.ServiceAccountFilePath);
        }

        [Fact]
        public void TheEnvironmentVariableWins_WhenBothAreSupplied()
        {
            var options = Resolve(new()
            {
                ["PushNotifications:ServiceAccountFilePath"] = "/keys/bound.json",
                ["FCM_SERVICE_ACCOUNT_FILE"] = "/keys/env.json",
            });

            Assert.Equal("/keys/env.json", options.ServiceAccountFilePath);
        }

        [Fact]
        public void NothingConfigured_LeavesTheSenderUnconfiguredRatherThanThrowing()
        {
            var options = Resolve(new());

            Assert.True(string.IsNullOrEmpty(options.ServiceAccountFilePath));
            Assert.True(string.IsNullOrEmpty(options.ServiceAccountJson));
        }

        [Fact]
        public void AProviderWithNoCredential_ReportsNotConfiguredInsteadOfFailingAtStartup()
        {
            var provider = new FcmAccessTokenProvider(Options.Create(new PushNotificationOptions()));

            Assert.False(provider.IsConfigured);
        }
    }
}
