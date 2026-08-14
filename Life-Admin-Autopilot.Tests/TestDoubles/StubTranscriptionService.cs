using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    public class StubTranscriptionService : ITranscriptionService
    {
        private readonly Func<TranscriptionRequest, Result<TranscriptionResult>> _responder;

        public StubTranscriptionService(Func<TranscriptionRequest, Result<TranscriptionResult>> responder)
        {
            _responder = responder;
        }

        public List<TranscriptionRequest> Requests { get; } = new();

        public Task<Result<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return Task.FromResult(_responder(request));
        }

        public static StubTranscriptionService Returning(string text, string? language = "en") =>
            new(_ => Result<TranscriptionResult>.Success(new TranscriptionResult
            {
                Text = text,
                DetectedLanguage = language,
                AudioDurationSeconds = 3.2,
                LatencyMs = 412
            }));

        public static StubTranscriptionService Failing(string errorCode, string message = "provider said no") =>
            new(_ => Result<TranscriptionResult>.Failure(new Error(errorCode, message)));

        /// <summary>
        /// A different answer per call, for the paths that call the provider twice —
        /// detect-then-pin. The last step repeats if asked for more, so a test only
        /// has to describe the calls it cares about.
        /// </summary>
        public static StubTranscriptionService Sequence(
            params Func<TranscriptionRequest, Result<TranscriptionResult>>[] steps)
        {
            var call = 0;
            return new(request => steps[Math.Min(call++, steps.Length - 1)](request));
        }

        public static Result<TranscriptionResult> Heard(string text, string? language = null) =>
            Result<TranscriptionResult>.Success(new TranscriptionResult
            {
                Text = text,
                DetectedLanguage = language,
                AudioDurationSeconds = 3.2,
                LatencyMs = 412
            });

        public static Result<TranscriptionResult> HeardNothing() =>
            Result<TranscriptionResult>.Failure(
                new Error(SpeechErrorCodes.EmptyTranscript, "The provider returned no speech for this audio."));
    }
}
