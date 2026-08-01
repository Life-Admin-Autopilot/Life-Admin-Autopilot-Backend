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
    }
}
