using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.DAL.Speech.Models.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Speech
{
    /// <summary>
    /// Transcribes with Azure AI Speech's FAST TRANSCRIPTION API.
    ///
    /// <para>
    /// <b>Why this API and not the short-audio one.</b> The classic REST endpoint
    /// (<c>/stt/speech/recognition/...</c>) caps directly transmitted audio at 60 seconds -
    /// a product limit, not a transfer one, so chunked encoding does not raise it. Our
    /// recorder allows five minutes and the 5 MiB ceiling admits ~2m45s of 16 kHz mono, so
    /// that endpoint would mean three or more independent recognition sessions with
    /// punctuation and sentence continuity restarting at every boundary. Fast transcription
    /// is one synchronous request, faster than real time, and its own ceiling is orders of
    /// magnitude above anything this product can produce.
    /// </para>
    ///
    /// <para>
    /// <b>It is more permissive about audio than the route it backs up.</b> WAV/PCM is the
    /// recommended input and there is no sample-rate, bit-depth or channel-count
    /// requirement on this API - the 16 kHz-mono rule people remember belongs to the
    /// short-audio endpoint, which enforces it through a codecs/samplerate content type.
    /// The client already emits 16 kHz mono PCM anyway, so nothing here resamples,
    /// transcodes or re-containers, and nothing should start.
    /// </para>
    /// </summary>
    public class AzureFastTranscriptionService : ITranscriptionService
    {
        /// <summary>
        /// Azure locales this service will pin to. Ordered so the first entry for a
        /// language is what a bare language code resolves to.
        ///
        /// <para>
        /// <b>ar-EG is first, and that is the point of the exercise.</b> The other provider
        /// has no Egyptian locale and has to collapse ar-EG onto ar-AR; Azure ships a real
        /// one, so an Egyptian user reaches the Egyptian acoustic model.
        /// </para>
        ///
        /// <para>
        /// Not the complete Azure list, which runs past a hundred entries. This is the set
        /// the product can actually reach - its own two languages, plus the locales a
        /// device might plausibly report - and adding to it is a one-line change.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedLocales =
        [
            "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-IE", "en-NZ", "en-ZA",
            "ar-EG", "ar-SA", "ar-AE", "ar-JO", "ar-KW", "ar-MA", "ar-QA", "ar-LB",
            "ar-DZ", "ar-BH", "ar-IQ", "ar-LY", "ar-OM", "ar-SY", "ar-TN", "ar-YE",
            "fr-FR", "fr-CA", "de-DE", "es-ES", "es-MX", "it-IT", "pt-BR", "pt-PT",
            "nl-NL", "ru-RU", "tr-TR", "he-IL", "hi-IN", "ur-PK", "fa-IR",
            "ja-JP", "ko-KR", "zh-CN", "zh-HK", "zh-TW",
            "pl-PL", "sv-SE", "da-DK", "nb-NO", "fi-FI", "cs-CZ", "el-GR", "ro-RO",
            "hu-HU", "uk-UA", "bg-BG", "hr-HR", "sk-SK", "sl-SI", "lt-LT", "lv-LV",
            "et-EE", "th-TH", "vi-VN", "id-ID", "ms-MY", "mt-MT", "sw-KE", "af-ZA"
        ];

        /// <summary>
        /// Detection ran and could not name a language. Azure treats these as errors;
        /// we do not.
        ///
        /// <para>
        /// A note that dies on a hard failure burns all four worker attempts and is marked
        /// failed, whereas the voice-note worker settles a note as <c>ready</c> on exactly
        /// <c>ASR_EMPTY_TRANSCRIPT</c>. "Nothing recognisable was said" is the honest
        /// reading of both of these, and it is the reading that does not cost the user four
        /// round trips to reach.
        /// </para>
        /// </summary>
        private static readonly string[] SilenceCodes =
        [
            "NoLanguageIdentified",
            "MultipleLanguagesIdentified"
        ];

        private readonly HttpClient _httpClient;
        private readonly AzureSpeechOptions _options;
        private readonly ILogger<AzureFastTranscriptionService> _logger;

        public AzureFastTranscriptionService(
            HttpClient httpClient,
            IOptions<AzureSpeechOptions> options,
            ILogger<AzureFastTranscriptionService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<Result<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            // Before any HTTP: an unconfigured environment is a supported state, and it
            // must cost zero requests. Never a startup throw - see docs/RUNNING.md.
            if (!_options.IsConfigured)
            {
                return Fail(
                    SpeechErrorCodes.NotConfigured,
                    "Azure Speech is not configured. Set AZURE_SPEECH_KEY and Speech:Azure:Endpoint.");
            }

            byte[] audio;
            using (var buffer = new MemoryStream())
            {
                await request.Audio.CopyToAsync(buffer, cancellationToken);
                audio = buffer.ToArray();
            }

            if (audio.Length == 0)
            {
                return Fail(SpeechErrorCodes.NoAudio, "The uploaded audio is empty.");
            }

            var locales = ResolveLocales(request.Language);

            // Content is disposed with the request message; MultipartFormDataContent owns
            // both parts and sets its own boundary. Setting Content-Type by hand here is a
            // documented way to break this - the header Microsoft's curl sample writes
            // carries no boundary parameter.
            using var content = new MultipartFormDataContent();

            var audioPart = new ByteArrayContent(audio);
            audioPart.Headers.ContentType = MediaTypeHeaderValue.Parse(
                string.IsNullOrWhiteSpace(request.ContentType) ? "audio/wav" : request.ContentType);
            content.Add(audioPart, "audio", FileNameFor(request));

            // A form field whose VALUE is JSON, not a JSON part.
            content.Add(
                new StringContent(
                    JsonSerializer.Serialize(new AzureTranscribeDefinition { Locales = locales }),
                    Encoding.UTF8),
                "definition");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri()) { Content = content };
            httpRequest.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);

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
                    $"Azure Speech did not respond within {_options.TimeoutSeconds}s.");
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
                    return FailFromBody(response.StatusCode, rawBody);
                }

                return ParseSuccessBody(rawBody, locales, stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// "auto" is a protocol sentinel, not an Azure locale - it is turned into a
        /// candidate list for language identification here.
        /// </summary>
        private string[] ResolveLocales(string? requested)
        {
            var normalized = LanguageNormalizer.Normalize(requested, SupportedLocales);

            if (!string.Equals(normalized, LanguageNormalizer.Auto, StringComparison.Ordinal))
            {
                return [normalized];
            }

            // Never an empty array: that selects Azure's multilingual model, which does not
            // support Arabic. See AzureSpeechOptions.AutoDetectLocales.
            return _options.AutoDetectLocales.Length > 0
                ? _options.AutoDetectLocales
                : ["en-US", "ar-EG"];
        }

        private Uri BuildUri() => new(
            $"{_options.Endpoint.TrimEnd('/')}/speechtotext/transcriptions:transcribe" +
            $"?api-version={Uri.EscapeDataString(_options.ApiVersion)}");

        /// <summary>
        /// Azure sniffs the content rather than trusting the name, and the voice-note path
        /// sends a FileName with no extension at all ("voice-note"). Giving the part an
        /// extension that matches its declared type is cheap insurance, not a requirement.
        /// </summary>
        private static string FileNameFor(TranscriptionRequest request)
        {
            var extension = request.ContentType switch
            {
                "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => ".wav",
                "audio/mpeg" or "audio/mp3" => ".mp3",
                "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
                "audio/aac" => ".aac",
                "audio/ogg" or "audio/opus" => ".ogg",
                "audio/webm" => ".webm",
                "audio/flac" => ".flac",
                _ => ".wav"
            };

            var name = string.IsNullOrWhiteSpace(request.FileName) ? "audio" : request.FileName;

            return Path.HasExtension(name) ? name : name + extension;
        }

        // Every failure is logged here, where the provider's own message is still intact.
        // Nothing reaches the caller as an unlogged error.
        private Result<TranscriptionResult> Fail(string code, string message)
        {
            _logger.LogWarning(
                "Transcription failed against Azure fast transcription: {ErrorCode} - {ErrorMessage}",
                code,
                message);

            return Result<TranscriptionResult>.Failure(new Error(code, message));
        }

        private Result<TranscriptionResult> FailFromBody(HttpStatusCode statusCode, string rawBody)
        {
            AzureErrorResponse? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<AzureErrorResponse>(rawBody);
            }
            catch (JsonException)
            {
                // Not JSON at all (an APIM gateway page, say) - fall back to the raw body
                // rather than losing the reason entirely.
            }

            var providerMessage = parsed?.Describe();
            var detailedCode = parsed?.DetailedCode();

            var message = string.IsNullOrWhiteSpace(providerMessage)
                ? $"HTTP {(int)statusCode}: {Truncate(rawBody)}"
                : $"HTTP {(int)statusCode}: {Truncate(providerMessage)}";

            // Detection that found no language comes back as an error status; it is not one.
            if (detailedCode is not null
                && SilenceCodes.Contains(detailedCode, StringComparer.OrdinalIgnoreCase))
            {
                return Fail(SpeechErrorCodes.EmptyTranscript, message);
            }

            return Fail(MapErrorCode(statusCode, detailedCode, rawBody), message);
        }

        /// <summary>
        /// Provider status to the codes the rest of the system already switches on. No new
        /// codes: <c>SpeechController.MapStatusCode</c>, <c>NemotronVoiceTranscriber.StatusFor</c>
        /// and <c>AsrAvailability.IsPermanent</c> all read this same vocabulary.
        /// </summary>
        private static string MapErrorCode(HttpStatusCode statusCode, string? detailedCode, string rawBody) =>
            statusCode switch
            {
                // Documented two contradictory ways by Microsoft (the REST table says 403 is
                // a MISSING credential and 401 an invalid one; the SDK troubleshooting page
                // treats them as interchangeable). Both are the same thing to us.
                //
                // The exception is the APIM-fronted quota refusal, which is also a 403 and
                // means something else entirely: waiting an hour helps, and the breaker
                // should close. Nothing in Microsoft's docs states what an exhausted Speech
                // quota returns, so this reads the body - replace it the moment a real one
                // is observed.
                HttpStatusCode.Forbidden when LooksLikeQuota(rawBody) => SpeechErrorCodes.QuotaExceeded,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SpeechErrorCodes.NotAuthorized,

                // Azure's 429 is usually autoscale lag on a resource that is fine, NOT a
                // spent allowance. Deliberately does not trip the availability breaker.
                HttpStatusCode.TooManyRequests => SpeechErrorCodes.RateLimited,

                HttpStatusCode.PaymentRequired => SpeechErrorCodes.QuotaExceeded,

                // InvalidAudio rather than UnsupportedFormat even for a format complaint:
                // UnsupportedFormat falls through the BLL's user-message switch and would
                // show the user the raw provider body.
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                    SpeechErrorCodes.InvalidAudio,

                // Undocumented for this API; kept because the alternative is a 413 mapping
                // to a generic error and an operator hunting for why.
                HttpStatusCode.RequestEntityTooLarge => SpeechErrorCodes.AudioTooLarge,

                HttpStatusCode.RequestTimeout => SpeechErrorCodes.Timeout,
                // MUST precede the >= 500 arm below.
                HttpStatusCode.GatewayTimeout => SpeechErrorCodes.Timeout,
                >= HttpStatusCode.InternalServerError => SpeechErrorCodes.Unavailable,

                // Unavailable, not GatewayError: the latter is not in the BLL's message
                // switch and would leak the raw response body to the user.
                _ => SpeechErrorCodes.Unavailable
            };

        private static bool LooksLikeQuota(string rawBody) =>
            rawBody.Contains("call volume quota", StringComparison.OrdinalIgnoreCase)
            || rawBody.Contains("quota will be replenished", StringComparison.OrdinalIgnoreCase)
            || rawBody.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase);

        // Provider bodies can be whole HTML pages; the log wants the reason, not the page.
        private static string Truncate(string value) =>
            value.Length <= 500 ? value : value[..500] + "...";

        private Result<TranscriptionResult> ParseSuccessBody(string rawBody, string[] locales, long latencyMs)
        {
            AzureTranscribeResult? parsed;
            try
            {
                // Read as a string then deserialize, rather than ReadFromJsonAsync: a 200
                // that is actually an HTML gateway page has to become a handled failure,
                // not a media-type exception.
                parsed = JsonSerializer.Deserialize<AzureTranscribeResult>(rawBody);
            }
            catch (JsonException ex)
            {
                return Fail(
                    SpeechErrorCodes.UnrecognizedResponseShape,
                    $"Azure returned a successful response that could not be parsed ({ex.Message}). Raw body: {Truncate(rawBody)}");
            }

            if (parsed is null)
            {
                return Fail(
                    SpeechErrorCodes.UnrecognizedResponseShape,
                    $"Azure returned an empty response body. Raw body: {Truncate(rawBody)}");
            }

            // One entry per channel, and we never ask for channel splitting - so this is
            // the whole transcript. Joined rather than indexed anyway, so that switching
            // `channels` on later degrades into a merged transcript instead of silently
            // discarding every channel but the first.
            var text = string.Join(
                " ",
                (parsed.CombinedPhrases ?? [])
                    .Select(phrase => phrase.Text?.Trim())
                    .Where(value => !string.IsNullOrEmpty(value)))
                .Trim();

            // A successful call that heard nothing is still a dead end for the Planning
            // Agent, so it is reported as a handled failure rather than an empty task.
            if (text.Length == 0)
            {
                return Fail(
                    SpeechErrorCodes.EmptyTranscript,
                    "The provider returned no speech for this audio.");
            }

            // A REAL detection, not an echo of what was asked for - which is why this is
            // populated even on an auto-detect run, where the other provider reports null.
            var detected = parsed.Phrases?
                .Select(phrase => phrase.Locale)
                .FirstOrDefault(locale => !string.IsNullOrWhiteSpace(locale));

            _logger.LogInformation(
                "Transcribed audio with Azure fast transcription in {LatencyMs}ms ({TranscriptLength} chars); asked {Locales}, heard {DetectedLanguage}",
                latencyMs,
                text.Length,
                string.Join(",", locales),
                detected ?? "unknown");

            return Result<TranscriptionResult>.Success(new TranscriptionResult
            {
                Text = text,
                DetectedLanguage = detected,
                AudioDurationSeconds = parsed.DurationMilliseconds is { } ms ? ms / 1000.0 : null,
                LatencyMs = latencyMs
            });
        }
    }
}
