using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.DAL.Speech.Models.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Speech
{
    public class DeepInfraTranscriptionService : ITranscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly SpeechOptions _options;
        private readonly ILogger<DeepInfraTranscriptionService> _logger;

        public DeepInfraTranscriptionService(
            HttpClient httpClient,
            IOptions<SpeechOptions> options,
            ILogger<DeepInfraTranscriptionService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<Result<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                return Fail(
                    SpeechErrorCodes.NotConfigured,
                    "No ASR provider token is configured. Set DEEPINFRA_TOKEN.");
            }

            using var content = await BuildMultipartContentAsync(request, cancellationToken);
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.InferenceBaseUrl.TrimEnd('/')}/{_options.ModelId}")
            {
                Content = content
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }
            // HttpClient reports its own timeout as a cancellation, so the caller's token is
            // what distinguishes "the user walked away" from "the provider never answered".
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return Fail(
                    SpeechErrorCodes.Timeout,
                    $"The ASR provider did not respond within {_options.TimeoutSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                return Fail(SpeechErrorCodes.NetworkError, ex.Message);
            }

            using (response)
            {
                var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    var error = ParseErrorBody(response.StatusCode, rawBody);
                    return Fail(error.Code, error.Message);
                }

                return ParseSuccessBody(rawBody, stopwatch.ElapsedMilliseconds);
            }
        }

        // The audio is buffered rather than streamed straight through: a retry has to be
        // able to re-send the same body, and a consumed upload stream cannot be replayed.
        // Uploads are capped well below memory-pressure territory before they get here.
        private async Task<MultipartFormDataContent> BuildMultipartContentAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await request.Audio.CopyToAsync(buffer, cancellationToken);

            var audioContent = new ByteArrayContent(buffer.ToArray());
            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);

            return new MultipartFormDataContent
            {
                { audioContent, "audio", request.FileName },
                { new StringContent(request.Language ?? _options.Language), "language" },
                { new StringContent("transcribe"), "task" }
            };
        }

        // Every failure is logged here, where the provider's own message is still intact.
        // Nothing reaches the caller as an unlogged error.
        private Result<TranscriptionResult> Fail(string code, string message)
        {
            _logger.LogWarning(
                "Transcription failed against {ModelId}: {ErrorCode} - {ErrorMessage}",
                _options.ModelId,
                code,
                message);

            return Result<TranscriptionResult>.Failure(new Error(code, message));
        }

        private static Error ParseErrorBody(HttpStatusCode statusCode, string rawBody)
        {
            string? providerMessage = null;
            try
            {
                var parsed = JsonSerializer.Deserialize<DeepInfraErrorResponse>(rawBody);
                providerMessage = parsed?.Error?.Message ?? parsed?.Detail;
            }
            catch (JsonException)
            {
                // FastAPI also returns detail as an array of objects; fall back to the raw
                // body rather than losing the reason entirely.
            }

            var message = string.IsNullOrWhiteSpace(providerMessage)
                ? $"HTTP {(int)statusCode}: {rawBody}"
                : $"HTTP {(int)statusCode}: {providerMessage}";

            return new Error(MapErrorCode(statusCode), message);
        }

        private static string MapErrorCode(HttpStatusCode statusCode) => statusCode switch
        {
            // The provider could not read the audio - wrong container, corrupt file, or
            // not mono as the model card requires.
            HttpStatusCode.BadRequest => SpeechErrorCodes.InvalidAudio,
            HttpStatusCode.UnprocessableEntity => SpeechErrorCodes.InvalidAudio,
            HttpStatusCode.RequestEntityTooLarge => SpeechErrorCodes.AudioTooLarge,
            HttpStatusCode.Unauthorized => SpeechErrorCodes.NotAuthorized,
            HttpStatusCode.Forbidden => SpeechErrorCodes.NotAuthorized,
            // Out of credit reads as a payment problem, but it is the same operator fix as
            // a bad token: nothing transcribes until someone tops the account up.
            HttpStatusCode.PaymentRequired => SpeechErrorCodes.NotAuthorized,
            HttpStatusCode.TooManyRequests => SpeechErrorCodes.RateLimited,
            HttpStatusCode.RequestTimeout => SpeechErrorCodes.Timeout,
            HttpStatusCode.GatewayTimeout => SpeechErrorCodes.Timeout,
            >= HttpStatusCode.InternalServerError => SpeechErrorCodes.Unavailable,
            _ => SpeechErrorCodes.GatewayError
        };

        private Result<TranscriptionResult> ParseSuccessBody(string rawBody, long latencyMs)
        {
            DeepInfraTranscriptionWireResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DeepInfraTranscriptionWireResponse>(rawBody);
            }
            catch (JsonException ex)
            {
                return Fail(
                    SpeechErrorCodes.UnrecognizedResponseShape,
                    $"The ASR provider returned a successful response that could not be parsed ({ex.Message}). Raw body: {rawBody}");
            }

            if (parsed is null)
            {
                return Fail(
                    SpeechErrorCodes.UnrecognizedResponseShape,
                    $"The ASR provider returned an empty response body. Raw body: {rawBody}");
            }

            var text = parsed.Text?.Trim() ?? string.Empty;

            // A successful call that heard nothing is still a dead end for the Planning
            // Agent, so it is reported as a handled failure rather than an empty task.
            if (text.Length == 0)
            {
                return Fail(
                    SpeechErrorCodes.EmptyTranscript,
                    "The ASR provider returned no speech for this audio.");
            }

            _logger.LogInformation(
                "Transcribed {AudioSeconds}s of audio with {ModelId} in {LatencyMs}ms ({TranscriptLength} chars, language {Language})",
                parsed.Duration,
                _options.ModelId,
                latencyMs,
                text.Length,
                parsed.Language ?? "unknown");

            return Result<TranscriptionResult>.Success(new TranscriptionResult
            {
                Text = text,
                DetectedLanguage = parsed.Language,
                AudioDurationSeconds = parsed.Duration,
                InferenceRuntimeMs = parsed.InferenceStatus?.RuntimeMs,
                CostUsd = parsed.InferenceStatus?.Cost,
                LatencyMs = latencyMs,
                Segments = parsed.Segments?
                    .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                    .Select(segment => new TranscriptionSegment
                    {
                        StartSeconds = segment.Start,
                        EndSeconds = segment.End,
                        Text = segment.Text!.Trim()
                    })
                    .ToList() ?? (IReadOnlyList<TranscriptionSegment>)Array.Empty<TranscriptionSegment>()
            });
        }
    }
}
