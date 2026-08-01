using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.DAL.Speech.Models.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Speech
{
    // Transcribes with NVIDIA Nemotron 3.5 ASR through Hugging Face's Inference Providers
    // router. Unlike an audio-capable LLM this is a real ASR: it transcribes what was said
    // rather than paraphrasing it, and it keeps Egyptian dialect instead of normalising it
    // into Modern Standard Arabic.
    public class NemotronTranscriptionService : ITranscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly SpeechOptions _options;
        private readonly ILogger<NemotronTranscriptionService> _logger;

        public NemotronTranscriptionService(
            HttpClient httpClient,
            IOptions<SpeechOptions> options,
            ILogger<NemotronTranscriptionService> logger)
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
                    "No transcription provider token is configured. Set HF_TOKEN.");
            }

            string audioBase64;
            using (var buffer = new MemoryStream())
            {
                await request.Audio.CopyToAsync(buffer, cancellationToken);
                audioBase64 = Convert.ToBase64String(buffer.ToArray());
            }

            if (audioBase64.Length == 0)
            {
                return Fail(SpeechErrorCodes.NoAudio, "The uploaded audio is empty.");
            }

            // Anything outside the provider's fixed locale set is a 422, so the requested
            // language is mapped onto it here rather than sent through as-is.
            var language = LanguageNormalizer.Normalize(request.Language, _options.DefaultLanguage);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.TranscriptionUrl)
            {
                Content = JsonContent.Create(new NemotronWireRequest
                {
                    AudioUrl = $"data:{request.ContentType};base64,{audioBase64}",
                    Language = language
                })
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
                    $"The transcription provider did not respond within {_options.TimeoutSeconds}s.");
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

                return ParseSuccessBody(rawBody, language, stopwatch.ElapsedMilliseconds);
            }
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
                providerMessage = JsonSerializer.Deserialize<HuggingFaceErrorResponse>(rawBody)?.Describe();
            }
            catch (JsonException)
            {
                // Not JSON at all (an HTML error page from the router, say) - fall back to
                // the raw body rather than losing the reason entirely.
            }

            var message = string.IsNullOrWhiteSpace(providerMessage)
                ? $"HTTP {(int)statusCode}: {Truncate(rawBody)}"
                : $"HTTP {(int)statusCode}: {Truncate(providerMessage)}";

            return new Error(MapErrorCode(statusCode), message);
        }

        private static string MapErrorCode(HttpStatusCode statusCode) => statusCode switch
        {
            // "You have depleted your monthly included credits" - retrying will not help,
            // and it is worth telling apart from a bad token when nothing transcribes.
            HttpStatusCode.PaymentRequired => SpeechErrorCodes.QuotaExceeded,
            HttpStatusCode.BadRequest => SpeechErrorCodes.InvalidAudio,
            HttpStatusCode.UnprocessableEntity => SpeechErrorCodes.InvalidAudio,
            HttpStatusCode.RequestEntityTooLarge => SpeechErrorCodes.AudioTooLarge,
            HttpStatusCode.Unauthorized => SpeechErrorCodes.NotAuthorized,
            HttpStatusCode.Forbidden => SpeechErrorCodes.NotAuthorized,
            HttpStatusCode.TooManyRequests => SpeechErrorCodes.RateLimited,
            HttpStatusCode.RequestTimeout => SpeechErrorCodes.Timeout,
            HttpStatusCode.GatewayTimeout => SpeechErrorCodes.Timeout,
            >= HttpStatusCode.InternalServerError => SpeechErrorCodes.Unavailable,
            _ => SpeechErrorCodes.GatewayError
        };

        // Provider bodies can be whole HTML pages; the log wants the reason, not the page.
        private static string Truncate(string value) =>
            value.Length <= 500 ? value : value[..500] + "...";

        private Result<TranscriptionResult> ParseSuccessBody(string rawBody, string language, long latencyMs)
        {
            NemotronWireResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<NemotronWireResponse>(rawBody);
            }
            catch (JsonException ex)
            {
                return Fail(
                    SpeechErrorCodes.UnrecognizedResponseShape,
                    $"The provider returned a successful response that could not be parsed ({ex.Message}). Raw body: {Truncate(rawBody)}");
            }

            if (parsed is null)
            {
                return Fail(
                    SpeechErrorCodes.UnrecognizedResponseShape,
                    $"The provider returned an empty response body. Raw body: {Truncate(rawBody)}");
            }

            var text = parsed.Output?.Trim() ?? string.Empty;

            // A successful call that heard nothing is still a dead end for the Planning
            // Agent, so it is reported as a handled failure rather than an empty task.
            if (text.Length == 0)
            {
                return Fail(
                    SpeechErrorCodes.EmptyTranscript,
                    "The provider returned no speech for this audio.");
            }

            if (parsed.Partial)
            {
                // Worth surfacing rather than silently returning half a command: the task
                // built from it would be wrong in a way the user may not notice.
                _logger.LogWarning(
                    "The provider returned a partial transcript for a {Language} recording ({TranscriptLength} chars)",
                    language,
                    text.Length);
            }

            _logger.LogInformation(
                "Transcribed {Language} audio with {ModelId} in {LatencyMs}ms ({TranscriptLength} chars)",
                language,
                _options.ModelId,
                latencyMs,
                text.Length);

            return Result<TranscriptionResult>.Success(new TranscriptionResult
            {
                Text = text,
                // Echoes what was asked for rather than what was detected: the provider
                // does not report a detected language, so "auto" stays unresolved.
                DetectedLanguage = language == LanguageNormalizer.Auto ? null : language,
                LatencyMs = latencyMs
            });
        }
    }
}
