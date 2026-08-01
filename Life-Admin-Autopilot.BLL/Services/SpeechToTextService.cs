using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class SpeechToTextService : ISpeechToTextService
    {
        private readonly ITranscriptionService _transcriptionService;
        private readonly SpeechOptions _options;
        private readonly ILogger<SpeechToTextService> _logger;

        public SpeechToTextService(
            ITranscriptionService transcriptionService,
            IOptions<SpeechOptions> options,
            ILogger<SpeechToTextService> logger)
        {
            _transcriptionService = transcriptionService;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<TranscriptionResponse> TranscribeAsync(
            AudioUpload audio,
            string? language = null,
            CancellationToken cancellationToken = default)
        {
            var rejection = Validate(audio);
            if (rejection is not null)
            {
                _logger.LogWarning(
                    "Rejected audio upload '{FileName}' ({ContentType}, {LengthBytes} bytes): {ErrorCode} - {ErrorMessage}",
                    audio.FileName,
                    audio.ContentType,
                    audio.LengthBytes,
                    rejection.ErrorCode,
                    rejection.ErrorMessage);

                return rejection;
            }

            var result = await _transcriptionService.TranscribeAsync(
                new TranscriptionRequest
                {
                    Audio = audio.Content,
                    FileName = audio.FileName,
                    ContentType = NormalizeContentType(audio.ContentType),
                    Language = language
                },
                cancellationToken);

            if (result.IsFailure)
            {
                var error = result.Error!;

                _logger.LogWarning(
                    "Transcription of '{FileName}' ({LengthBytes} bytes) failed: {ErrorCode} - {ErrorMessage}",
                    audio.FileName,
                    audio.LengthBytes,
                    error.Code,
                    error.Message);

                return TranscriptionResponse.Fail(error.Code, ToUserMessage(error.Code, error.Message));
            }

            var transcription = result.Value!;

            // The transcript itself is the user's own words and stays out of the logs -
            // its length and timing are enough to diagnose a bad recording.
            _logger.LogInformation(
                "Transcribed '{FileName}' in {LatencyMs}ms: {TranscriptLength} chars, language {Language}",
                audio.FileName,
                transcription.LatencyMs,
                transcription.Text.Length,
                transcription.DetectedLanguage ?? "unknown");

            return TranscriptionResponse.Success(
                transcription.Text,
                transcription.DetectedLanguage,
                transcription.AudioDurationSeconds,
                transcription.LatencyMs);
        }

        // Caught here rather than at the provider so obviously-unusable audio never costs
        // an inference call or the user a round trip.
        private TranscriptionResponse? Validate(AudioUpload audio)
        {
            if (audio.LengthBytes <= 0)
            {
                return TranscriptionResponse.Fail(
                    SpeechErrorCodes.NoAudio,
                    "No audio was uploaded.");
            }

            if (audio.LengthBytes > _options.MaxAudioBytes)
            {
                return TranscriptionResponse.Fail(
                    SpeechErrorCodes.AudioTooLarge,
                    $"The recording is larger than the {_options.MaxAudioBytes / (1024 * 1024)} MB limit.");
            }

            var contentType = NormalizeContentType(audio.ContentType);
            if (!_options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            {
                return TranscriptionResponse.Fail(
                    SpeechErrorCodes.UnsupportedFormat,
                    $"'{contentType}' is not a supported audio format. Record mono WAV.");
            }

            return null;
        }

        // Browsers and Capacitor send parameters on the media type (audio/wav;codecs=1),
        // which would otherwise fail an exact-match check against the allow list.
        private static string NormalizeContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return string.Empty;
            }

            var separatorIndex = contentType.IndexOf(';');

            return (separatorIndex < 0 ? contentType : contentType[..separatorIndex]).Trim();
        }

        // Provider messages can carry raw HTTP bodies and internal detail, so anything
        // caused by our side of the integration is replaced with something a user can act
        // on. The precise code and provider text are already in the logs.
        private static string ToUserMessage(string errorCode, string providerMessage) => errorCode switch
        {
            SpeechErrorCodes.EmptyTranscript => "We could not hear anything in that recording. Please try again.",
            SpeechErrorCodes.InvalidAudio => "That recording could not be read. Please record again in mono WAV.",
            SpeechErrorCodes.Timeout => "Transcription took too long. Please try again.",
            SpeechErrorCodes.RateLimited => "Voice input is busy right now. Please try again in a moment.",
            SpeechErrorCodes.QuotaExceeded => "Voice input is not available right now.",
            SpeechErrorCodes.Unavailable => "Voice input is temporarily unavailable. Please try again shortly.",
            SpeechErrorCodes.NetworkError => "Voice input is temporarily unreachable. Please try again shortly.",
            SpeechErrorCodes.NotAuthorized => "Voice input is not available right now.",
            SpeechErrorCodes.NotConfigured => "Voice input is not available right now.",
            _ => providerMessage
        };
    }
}
