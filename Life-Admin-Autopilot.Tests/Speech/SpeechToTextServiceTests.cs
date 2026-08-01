using System.Text;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class SpeechToTextServiceTests
    {
        [Fact]
        public async Task TranscribeAsync_ReturnsTheTranscript_WhenTheProviderSucceeds()
        {
            var provider = StubTranscriptionService.Returning("Book a dentist appointment on Tuesday");
            var service = CreateService(provider, out _);

            var response = await service.TranscribeAsync(Audio());

            Assert.True(response.Succeeded);
            Assert.Equal("Book a dentist appointment on Tuesday", response.Transcript);
            Assert.Equal("en", response.DetectedLanguage);
            Assert.Null(response.ErrorCode);
        }

        [Fact]
        public async Task TranscribeAsync_PassesTheCallersLanguageThroughToTheProvider()
        {
            var provider = StubTranscriptionService.Returning("موعد الطبيب", language: "ar");
            var service = CreateService(provider, out _);

            var response = await service.TranscribeAsync(Audio(), language: "ar");

            Assert.Equal("ar", provider.Requests.Single().Language);
            Assert.Equal("ar", response.DetectedLanguage);
        }

        [Fact]
        public async Task TranscribeAsync_RejectsAnEmptyUpload_WithoutCallingTheProvider()
        {
            var provider = StubTranscriptionService.Returning("never reached");
            var service = CreateService(provider, out var logger);

            var response = await service.TranscribeAsync(Audio(lengthBytes: 0));

            Assert.False(response.Succeeded);
            Assert.Equal(SpeechErrorCodes.NoAudio, response.ErrorCode);
            Assert.Empty(provider.Requests);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_RejectsAnOversizedUpload_WithoutCallingTheProvider()
        {
            var provider = StubTranscriptionService.Returning("never reached");
            var service = CreateService(provider, out _, maxAudioBytes: 1024);

            var response = await service.TranscribeAsync(Audio(lengthBytes: 2048));

            Assert.Equal(SpeechErrorCodes.AudioTooLarge, response.ErrorCode);
            Assert.Empty(provider.Requests);
        }

        // Capacitor's recorder defaults to AAC/m4a, which this model does not accept - the
        // rejection has to be clear rather than a confusing provider-side failure.
        [Fact]
        public async Task TranscribeAsync_RejectsAnUnsupportedFormat_WithAnActionableMessage()
        {
            var provider = StubTranscriptionService.Returning("never reached");
            var service = CreateService(provider, out _);

            var response = await service.TranscribeAsync(Audio(contentType: "audio/aac"));

            Assert.Equal(SpeechErrorCodes.UnsupportedFormat, response.ErrorCode);
            Assert.Contains("mono WAV", response.ErrorMessage);
            Assert.Empty(provider.Requests);
        }

        [Fact]
        public async Task TranscribeAsync_AcceptsAContentTypeThatCarriesParameters()
        {
            var provider = StubTranscriptionService.Returning("Renew my passport");
            var service = CreateService(provider, out _);

            var response = await service.TranscribeAsync(Audio(contentType: "audio/wav; codecs=1"));

            Assert.True(response.Succeeded);
            Assert.Equal("audio/wav", provider.Requests.Single().ContentType);
        }

        // Acceptance criterion: a failed ASR call is handled, not thrown.
        [Theory]
        [InlineData(SpeechErrorCodes.Timeout)]
        [InlineData(SpeechErrorCodes.Unavailable)]
        [InlineData(SpeechErrorCodes.RateLimited)]
        [InlineData(SpeechErrorCodes.NotAuthorized)]
        [InlineData(SpeechErrorCodes.NetworkError)]
        [InlineData(SpeechErrorCodes.EmptyTranscript)]
        public async Task TranscribeAsync_ReturnsAHandledFailure_ForEveryProviderError(string errorCode)
        {
            var provider = StubTranscriptionService.Failing(errorCode);
            var service = CreateService(provider, out var logger);

            var response = await service.TranscribeAsync(Audio());

            Assert.False(response.Succeeded);
            Assert.Equal(errorCode, response.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(response.ErrorMessage));
            Assert.Single(logger.Warnings);
        }

        // Provider messages can contain raw HTTP bodies; what reaches the user should not.
        [Fact]
        public async Task TranscribeAsync_ReplacesProviderDetailWithAUserFacingMessage()
        {
            var provider = StubTranscriptionService.Failing(
                SpeechErrorCodes.Unavailable,
                "HTTP 503: {\"detail\":\"upstream worker pool exhausted\"}");
            var service = CreateService(provider, out var logger);

            var response = await service.TranscribeAsync(Audio());

            Assert.DoesNotContain("upstream worker pool", response.ErrorMessage);
            Assert.Contains("try again", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            // The provider's own wording is still recoverable from the logs.
            Assert.Contains(logger.Warnings, warning => warning.Message.Contains("upstream worker pool"));
        }

        // The transcript is the user's own words - diagnosable without being logged.
        [Fact]
        public async Task TranscribeAsync_DoesNotLogTheTranscript()
        {
            var provider = StubTranscriptionService.Returning("Transfer two thousand pounds to my landlord");
            var service = CreateService(provider, out var logger);

            await service.TranscribeAsync(Audio());

            Assert.All(logger.Entries, entry => Assert.DoesNotContain("landlord", entry.Message));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("43 chars"));
        }

        private static AudioUpload Audio(
            long lengthBytes = 2048,
            string contentType = "audio/wav",
            string fileName = "command.wav") =>
            new(new MemoryStream(Encoding.UTF8.GetBytes("RIFF....fake wav bytes")), fileName, contentType, lengthBytes);

        private static SpeechToTextService CreateService(
            StubTranscriptionService provider,
            out RecordingLogger<SpeechToTextService> logger,
            long maxAudioBytes = 10 * 1024 * 1024)
        {
            logger = new RecordingLogger<SpeechToTextService>();

            return new SpeechToTextService(
                provider,
                Options.Create(new SpeechOptions { MaxAudioBytes = maxAudioBytes }),
                logger);
        }
    }
}
