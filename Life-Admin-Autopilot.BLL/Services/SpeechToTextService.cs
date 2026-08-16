using Life_Admin_Autopilot.DAL.Common;
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

            // Buffered because the audio may be sent TWICE (see the retries below) and a
            // request stream can only be read once.
            using var buffered = new MemoryStream();
            await audio.Content.CopyToAsync(buffered, cancellationToken);

            return await RunAsync(buffered, audio, language, cancellationToken);
        }

        // The upload gate is the ONLY thing this skips - see the interface for why the
        // voice-note worker must not be re-gated. Everything after it is shared, so the
        // two surfaces cannot disagree about how a bilingual recording is handled.
        public async Task<TranscriptionResponse> TranscribeStoredAsync(
            byte[] audio,
            string contentType,
            string? language = null,
            CancellationToken cancellationToken = default)
        {
            if (audio.Length == 0)
            {
                return TranscriptionResponse.Fail(SpeechErrorCodes.NoAudio, "No audio was uploaded.");
            }

            // Owns the stream: nothing else can, since the caller handed us bytes.
            using var buffered = new MemoryStream(audio, writable: false);

            return await RunAsync(
                buffered,
                new AudioUpload(buffered, "voice-note", contentType, audio.LongLength),
                language,
                cancellationToken);
        }

        private async Task<TranscriptionResponse> RunAsync(
            MemoryStream buffered,
            AudioUpload audio,
            string? language,
            CancellationToken cancellationToken)
        {
            // DETECT FIRST, PIN SECOND.
            //
            // `language` arrives from the app's active locale, which describes the
            // INTERFACE, not the sentence just spoken. Kitto is bilingual by design, so
            // a user reading English chrome and dictating Arabic is the normal case —
            // and pinning their Arabic to en-US makes the provider return an empty
            // transcript, which reaches them as "we could not hear anything" for a
            // recording they can hear perfectly. Measured over four consecutive real
            // recordings: pinned to the UI locale 0/4 usable, auto-detect 4/4.
            //
            // So the locale is treated as a HINT for repair, never as an assertion about
            // the audio. In the common case this is also one inference call instead of
            // two, which matters while the ASR quota is the scarce resource.
            var result = await SendAsync(buffered, audio, LanguageNormalizer.Auto, cancellationToken);

            if (!IsAuto(language))
            {
                // Detection heard nothing. Worth one pinned attempt before giving up —
                // a very short or heavily accented clip is the case where the hint helps.
                if (IsEmptyTranscript(result))
                {
                    _logger.LogInformation(
                        "Auto-detect found no speech; retrying pinned to '{Language}'.",
                        language);

                    result = await SendAsync(buffered, audio, language, cancellationToken);
                }

                // Detection heard something, but transliterated it. docs/speech-to-text.md
                // records this: Egyptian Arabic under auto-detect comes back as Latin
                // script. That is a WORSE failure than an error because it looks like a
                // transcript, so the hint is spent repairing it.
                else if (NeedsScriptRepair(result, language))
                {
                    _logger.LogInformation(
                        "Auto-detect returned Latin script for an '{Language}' user; retrying pinned.",
                        language);

                    var pinned = await SendAsync(buffered, audio, language, cancellationToken);

                    // Only take the pinned answer if it actually produced one — a failed
                    // repair must not discard the transcript we already have.
                    if (pinned.IsSuccess) result = pinned;
                }
            }

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

        private Task<Result<TranscriptionResult>> SendAsync(
            MemoryStream buffered,
            AudioUpload audio,
            string? language,
            CancellationToken cancellationToken)
        {
            buffered.Position = 0;
            return _transcriptionService.TranscribeAsync(
                new TranscriptionRequest
                {
                    Audio = buffered,
                    FileName = audio.FileName,
                    ContentType = NormalizeContentType(audio.ContentType),
                    Language = language
                },
                cancellationToken);
        }

        private static bool IsEmptyTranscript(Result<TranscriptionResult> result) =>
            result.IsFailure
            && string.Equals(result.Error!.Code, SpeechErrorCodes.EmptyTranscript, StringComparison.Ordinal);

        // "auto" is what LanguageNormalizer emits for an unknown or absent locale, so
        // there is no hint to repair with and no second call worth making.
        private static bool IsAuto(string? language) =>
            string.IsNullOrWhiteSpace(language)
            || string.Equals(language, LanguageNormalizer.Auto, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// An Arabic-locale user whose transcript came back with no Arabic in it at all.
        ///
        /// <para>
        /// Scoped to Arabic deliberately. This is not a general "did detection pick the
        /// right language" check — it cannot be, since a bilingual user really may have
        /// spoken English. It fires only on the one documented provider failure, where
        /// Arabic speech is romanised into Latin letters instead of being rejected, and
        /// where an Arabic-reading user is therefore guaranteed to be looking at
        /// something they cannot use.
        /// </para>
        /// </summary>
        private static bool NeedsScriptRepair(Result<TranscriptionResult> result, string? language)
        {
            if (result.IsFailure) return false;
            if (language is null || !language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)) return false;

            var text = result.Value!.Text;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Unicode ESCAPES, not literal characters. An Arabic range written inline
            // has already been mangled once in this repo by a non-UTF-8 toolchain, and
            // a corrupted range here would silently disable the check rather than fail
            // the build. The ASCII bound is a control character that renders as nothing
            // in an editor, which is worse again.
            const char arabicFirst = '؀';  // start of the Arabic block
            const char arabicLast = 'ۿ';   // end of it
            const char asciiEnd = '';     // one past 'z'

            var hasArabic = false;
            var hasLatin = false;
            foreach (var ch in text)
            {
                if (ch is >= arabicFirst and <= arabicLast) hasArabic = true;
                else if (ch < asciiEnd && char.IsLetter(ch)) hasLatin = true;
            }

            return hasLatin && !hasArabic;
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
