using System.Text;
using Life_Admin_Autopilot.BLL;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Extensions;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class SpeechWiringTests
    {
        [Fact]
        public void SpeechToTextServiceAndItsTransportResolveFromTheContainer()
        {
            using var provider = BuildProvider();
            using var scope = provider.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISpeechToTextService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITranscriptionService>());
        }

        // An environment with no provider token is a supported state: the API starts, and
        // a transcription attempt reports why instead of throwing (NFR-8).
        [Fact]
        public async Task TranscribingWithoutATokenReportsAHandledFailure()
        {
            using var provider = BuildProvider();
            using var scope = provider.CreateScope();
            var speechToText = scope.ServiceProvider.GetRequiredService<ISpeechToTextService>();

            var response = await speechToText.TranscribeAsync(new AudioUpload(
                new MemoryStream(Encoding.UTF8.GetBytes("RIFF....fake wav bytes")),
                "command.wav",
                "audio/wav",
                2048));

            Assert.False(response.Succeeded);
            Assert.Equal(SpeechErrorCodes.NotConfigured, response.ErrorCode);
        }

        private static ServiceProvider BuildProvider()
        {
            var configuration = new ConfigurationBuilder().Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSpeechServices(configuration);
            services.AddBusinessLogicLayer(configuration);

            return services.BuildServiceProvider(validateScopes: true);
        }
    }
}
