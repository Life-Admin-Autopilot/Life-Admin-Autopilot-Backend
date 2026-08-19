using System.Text;
using Life_Admin_Autopilot.BLL;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Extensions;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Microsoft.Extensions.Configuration;
using Life_Admin_Autopilot.DAL.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // Both transports resolve on their own too, so a broken typed-client registration
        // fails here rather than at the first recording.
        [Fact]
        public void BothTransportsResolveFromTheContainer()
        {
            using var provider = BuildProvider();
            using var scope = provider.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetRequiredService<NemotronTranscriptionService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<AzureFastTranscriptionService>());

            // The wrapper is what answers the interface - nothing above the seam should be
            // able to reach one provider directly.
            Assert.IsType<FailoverTranscriptionService>(
                scope.ServiceProvider.GetRequiredService<ITranscriptionService>());
        }

        // Which providers are dry has to outlive a request, or every call would start from
        // "we have not tried yet" and the sidelining would never fire.
        [Fact]
        public void ProviderHealthIsSharedAcrossScopes()
        {
            using var provider = BuildProvider();
            using var first = provider.CreateScope();
            using var second = provider.CreateScope();

            Assert.Same(
                first.ServiceProvider.GetRequiredService<ProviderHealth>(),
                second.ServiceProvider.GetRequiredService<ProviderHealth>());
        }

        // An Azure key with no endpoint is not configuration - it is a guaranteed 401 - so
        // the deployment still reports NotConfigured rather than spending a round trip
        // discovering that.
        [Fact]
        public async Task TranscribingWithAKeyButNoEndpointStillReportsNotConfigured()
        {
            using var provider = BuildProvider(new Dictionary<string, string?>
            {
                ["AZURE_SPEECH_KEY"] = "a-key-with-nowhere-to-go"
            });
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

        // The endpoint is accepted as a flat key beside AZURE_SPEECH_KEY so both halves sit
        // on adjacent lines of one .env, rather than one being flat and the other nested.
        [Fact]
        public void TheAzureEndpointCanBeConfiguredAsAFlatKey()
        {
            using var provider = BuildProvider(new Dictionary<string, string?>
            {
                ["AZURE_SPEECH_KEY"] = "a-key",
                ["AZURE_SPEECH_ENDPOINT"] = "https://kitto-speech.cognitiveservices.azure.com"
            });

            var options = provider.GetRequiredService<IOptions<AzureSpeechOptions>>().Value;

            Assert.True(options.IsConfigured);
            Assert.Equal("https://kitto-speech.cognitiveservices.azure.com", options.Endpoint);
        }

        // Configuration decides which provider is billed first, because a Hugging Face
        // account with no credits turns "fallback" into a wasted call on every request.
        [Fact]
        public void TheConfiguredPrimaryProviderIsTriedFirst()
        {
            using var provider = BuildProvider(new Dictionary<string, string?>
            {
                ["Speech:PrimaryProvider"] = "azure"
            });
            using var scope = provider.CreateScope();

            var order = FailoverTranscriptionService.InPreferenceOrder(
                scope.ServiceProvider.GetRequiredService<IOptions<SpeechOptions>>().Value.PrimaryProvider,
                new TranscriptionProvider(FailoverTranscriptionService.Nemotron, null!),
                new TranscriptionProvider(FailoverTranscriptionService.Azure, null!));

            Assert.Equal(FailoverTranscriptionService.Azure, order[0].Name);
        }

        private static ServiceProvider BuildProvider(Dictionary<string, string?>? settings = null)
        {
            var builder = new ConfigurationBuilder();
            if (settings is not null)
            {
                builder.AddInMemoryCollection(settings);
            }

            var configuration = builder.Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSpeechServices(configuration);
            services.AddBusinessLogicLayer(configuration);

            return services.BuildServiceProvider(validateScopes: true);
        }
    }
}
