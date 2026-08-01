using System.Net;
using System.Text;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class DeepInfraTranscriptionServiceTests
    {
        private const string ModelId = "nvidia/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b";

        private const string SuccessBody =
            """
            {"text":"Renew my passport next Friday","language":"en","duration":3.2,
             "segments":[{"start":0.0,"end":3.2,"text":"Renew my passport next Friday"}],
             "inference_status":{"status":"succeeded","runtime_ms":412,"cost":0.0000107}}
            """;

        [Fact]
        public async Task TranscribeAsync_ReturnsTheTranscript_WhenTheProviderSucceeds()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsSuccess);
            Assert.Equal("Renew my passport next Friday", result.Value!.Text);
            Assert.Equal("en", result.Value.DetectedLanguage);
            Assert.Equal(3.2, result.Value.AudioDurationSeconds);
            Assert.Equal(412, result.Value.InferenceRuntimeMs);
            Assert.Single(result.Value.Segments);
        }

        [Fact]
        public async Task TranscribeAsync_PostsTheAudioToTheConfiguredModel()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request());

            Assert.Equal($"https://provider.example/v1/inference/{ModelId}", handler.LastRequestUri!.ToString());
            Assert.Equal("Bearer test-token", handler.LastAuthorizationHeader);
            Assert.Contains("name=audio", handler.LastRequestBody!);
            Assert.Contains("name=language", handler.LastRequestBody);
        }

        // Acceptance criterion: a timed-out ASR call is a handled error, not a crash.
        [Fact]
        public async Task TranscribeAsync_ReturnsATimeoutError_WhenTheProviderNeverAnswers()
        {
            // HttpClient surfaces its own timeout as a TaskCanceledException.
            var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("The request timed out."));
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsFailure);
            Assert.Equal(SpeechErrorCodes.Timeout, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        // A user who navigated away is not a provider timeout, and must not be logged or
        // reported as one.
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

        [Theory]
        [InlineData(HttpStatusCode.BadRequest, SpeechErrorCodes.InvalidAudio)]
        [InlineData(HttpStatusCode.UnprocessableEntity, SpeechErrorCodes.InvalidAudio)]
        [InlineData(HttpStatusCode.Unauthorized, SpeechErrorCodes.NotAuthorized)]
        [InlineData(HttpStatusCode.PaymentRequired, SpeechErrorCodes.NotAuthorized)]
        [InlineData(HttpStatusCode.TooManyRequests, SpeechErrorCodes.RateLimited)]
        [InlineData(HttpStatusCode.GatewayTimeout, SpeechErrorCodes.Timeout)]
        [InlineData(HttpStatusCode.InternalServerError, SpeechErrorCodes.Unavailable)]
        [InlineData(HttpStatusCode.ServiceUnavailable, SpeechErrorCodes.Unavailable)]
        public async Task TranscribeAsync_MapsProviderStatusCodesToStableErrorCodes(
            HttpStatusCode statusCode,
            string expectedErrorCode)
        {
            var handler = new StubHttpMessageHandler(statusCode, """{"detail":"provider rejected the request"}""");
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(expectedErrorCode, result.Error!.Code);
            Assert.Contains("provider rejected the request", result.Error.Message);
            Assert.Single(logger.Warnings);
        }

        // FastAPI returns detail as an array for validation errors; that must not throw on
        // the way to reporting the failure.
        [Fact]
        public async Task TranscribeAsync_StillReportsAFailure_WhenTheErrorBodyIsAnUnexpectedShape()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.UnprocessableEntity,
                """{"detail":[{"loc":["body","audio"],"msg":"field required","type":"value_error.missing"}]}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.InvalidAudio, result.Error!.Code);
            Assert.Contains("field required", result.Error.Message);
        }

        // Silence is a successful call with nothing to plan from - the user is asked to
        // speak again rather than handed an empty task.
        [Fact]
        public async Task TranscribeAsync_ReportsAnEmptyTranscript_WhenTheProviderHeardNothing()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"text":"   ","language":"en"}""");
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_ReportsUnrecognizedShape_WhenTheSuccessBodyIsNotJson()
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
            var service = new DeepInfraTranscriptionService(
                new HttpClient(handler),
                Options.Create(new SpeechOptions { ApiKey = string.Empty }),
                new RecordingLogger<DeepInfraTranscriptionService>());

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NotConfigured, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }

        private static TranscriptionRequest Request() => new()
        {
            Audio = new MemoryStream(Encoding.UTF8.GetBytes("RIFF....fake wav bytes")),
            FileName = "command.wav",
            ContentType = "audio/wav"
        };

        private static (DeepInfraTranscriptionService Service, RecordingLogger<DeepInfraTranscriptionService> Logger) CreateService(
            HttpMessageHandler handler)
        {
            var logger = new RecordingLogger<DeepInfraTranscriptionService>();
            var service = new DeepInfraTranscriptionService(
                new HttpClient(handler),
                Options.Create(new SpeechOptions
                {
                    InferenceBaseUrl = "https://provider.example/v1/inference",
                    ModelId = ModelId,
                    ApiKey = "test-token",
                    Language = "auto"
                }),
                logger);

            return (service, logger);
        }
    }
}
