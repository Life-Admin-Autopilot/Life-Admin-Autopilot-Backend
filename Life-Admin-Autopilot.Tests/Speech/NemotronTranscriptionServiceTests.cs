using System.Net;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class NemotronTranscriptionServiceTests
    {
        // Captured verbatim from the live provider.
        private const string SuccessBody = """{"output":"Renew my passport next Friday.","partial":false}""";

        [Fact]
        public async Task TranscribeAsync_ReturnsTheTranscript_WhenTheProviderSucceeds()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsSuccess);
            Assert.Equal("Renew my passport next Friday.", result.Value!.Text);
        }

        // Multipart and raw-byte uploads are both rejected by this route - the audio has
        // to be a data URI in JSON. Asserted so a refactor cannot quietly break it.
        [Fact]
        public async Task TranscribeAsync_SendsTheAudioAsADataUri()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request());

            using var sent = JsonDocument.Parse(handler.LastRequestBody!);
            var audioUrl = sent.RootElement.GetProperty("audio_url").GetString()!;

            Assert.StartsWith("data:audio/wav;base64,", audioUrl);
            Assert.Equal("Bearer test-token", handler.LastAuthorizationHeader);
        }

        [Fact]
        public async Task TranscribeAsync_UsesTheUploadsOwnContentTypeInTheDataUri()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request(contentType: "audio/mpeg"));

            Assert.Contains("data:audio/mpeg;base64,", handler.LastRequestBody!);
        }

        // Auto-detection collapses into Latin transliteration on Arabic, so the caller's
        // locale has to reach the provider - normalised to a value it accepts.
        [Theory]
        [InlineData("ar-EG", "ar-AR")]
        [InlineData("ar", "ar-AR")]
        [InlineData("en-GB", "en-GB")]
        [InlineData("nonsense", "auto")]
        [InlineData(null, "auto")]
        public async Task TranscribeAsync_NormalisesTheRequestedLanguage(string? requested, string expected)
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request(language: requested));

            using var sent = JsonDocument.Parse(handler.LastRequestBody!);
            Assert.Equal(expected, sent.RootElement.GetProperty("language").GetString());
        }

        [Fact]
        public async Task TranscribeAsync_ReportsTheLanguageItAskedFor_ButNotWhenAuto()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var pinned = await service.TranscribeAsync(Request(language: "ar-EG"));
            var auto = await service.TranscribeAsync(Request());

            Assert.Equal("ar-AR", pinned.Value!.DetectedLanguage);
            // The provider reports no detected language, so "auto" must stay unresolved
            // rather than being reported as a language we never confirmed.
            Assert.Null(auto.Value!.DetectedLanguage);
        }

        // Acceptance criterion: a timed-out ASR call is a handled error, not a crash.
        [Fact]
        public async Task TranscribeAsync_ReturnsATimeoutError_WhenTheProviderNeverAnswers()
        {
            var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("The request timed out."));
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.Timeout, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        // A user who navigated away is not a provider timeout, and must not be logged as one.
        [Fact]
        public async Task TranscribeAsync_PropagatesCancellation_WhenTheCallerCancels()
        {
            var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("cancelled"));
            var (service, logger) = CreateService(handler);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.TranscribeAsync(Request(), cancellation.Token));

            Assert.Empty(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_ReturnsANetworkError_WhenTheProviderIsUnreachable()
        {
            var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("no such host"));
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NetworkError, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        // Captured verbatim from the live provider. This one will happen in practice, so
        // it gets its own code rather than being lumped in with auth failures.
        [Fact]
        public async Task TranscribeAsync_ReportsQuotaExceeded_WhenTheIncludedCreditsAreGone()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.PaymentRequired,
                """{"error":"You have depleted your monthly included credits. Purchase pre-paid credits to continue using Inference Providers."}""");
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.QuotaExceeded, result.Error!.Code);
            Assert.Contains("depleted your monthly included credits", result.Error.Message);
            Assert.Single(logger.Warnings);
        }

        // The router's own rejection shape, also captured live.
        [Fact]
        public async Task TranscribeAsync_SurfacesTheRouterReason_WhenAProviderCannotServeTheModel()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.BadRequest,
                """{"error":"Model not supported by provider hf-inference"}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.InvalidAudio, result.Error!.Code);
            Assert.Contains("Model not supported by provider", result.Error.Message);
        }

        // The provider's validation shape, captured live by sending an invalid locale.
        [Fact]
        public async Task TranscribeAsync_StillReportsAFailure_WhenTheErrorBodyIsAValidationArray()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.UnprocessableEntity,
                """{"detail":[{"type":"literal_error","loc":["body","language"],"msg":"Input should be 'auto', 'en-US'"}]}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.InvalidAudio, result.Error!.Code);
            Assert.Contains("literal_error", result.Error.Message);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, SpeechErrorCodes.NotAuthorized)]
        [InlineData(HttpStatusCode.TooManyRequests, SpeechErrorCodes.RateLimited)]
        [InlineData(HttpStatusCode.InternalServerError, SpeechErrorCodes.Unavailable)]
        [InlineData(HttpStatusCode.ServiceUnavailable, SpeechErrorCodes.Unavailable)]
        [InlineData(HttpStatusCode.GatewayTimeout, SpeechErrorCodes.Timeout)]
        public async Task TranscribeAsync_MapsProviderStatusCodesToStableErrorCodes(
            HttpStatusCode statusCode,
            string expectedErrorCode)
        {
            var handler = new StubHttpMessageHandler(statusCode, """{"error":"nope"}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(expectedErrorCode, result.Error!.Code);
        }

        [Fact]
        public async Task TranscribeAsync_ReportsAnEmptyTranscript_WhenTheProviderHeardNothing()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"output":"   ","partial":false}""");
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        // Half a command would build a wrong task the user might not notice, so it is
        // returned but flagged in the log.
        [Fact]
        public async Task TranscribeAsync_WarnsButStillReturns_WhenTheTranscriptIsPartial()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """{"output":"Renew my","partial":true}""");
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsSuccess);
            Assert.Equal("Renew my", result.Value!.Text);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_ReportsUnrecognizedShape_WhenTheBodyIsNotJson()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "<html>gateway</html>");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.UnrecognizedResponseShape, result.Error!.Code);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithoutCallingTheProvider_WhenNoTokenIsConfigured()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var service = new NemotronTranscriptionService(
                new HttpClient(handler),
                Options.Create(new SpeechOptions { ApiKey = string.Empty }),
                new RecordingLogger<NemotronTranscriptionService>());

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NotConfigured, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }

        private static TranscriptionRequest Request(string contentType = "audio/wav", string? language = null) => new()
        {
            Audio = new MemoryStream(Encoding.UTF8.GetBytes("RIFF....fake wav bytes")),
            FileName = "command.wav",
            ContentType = contentType,
            Language = language
        };

        private static (NemotronTranscriptionService Service, RecordingLogger<NemotronTranscriptionService> Logger) CreateService(
            HttpMessageHandler handler)
        {
            var logger = new RecordingLogger<NemotronTranscriptionService>();
            var service = new NemotronTranscriptionService(
                new HttpClient(handler),
                Options.Create(new SpeechOptions
                {
                    TranscriptionUrl = "https://router.example/fal-ai/nemotron/asr",
                    ApiKey = "test-token",
                    DefaultLanguage = "auto"
                }),
                logger);

            return (service, logger);
        }
    }
}
