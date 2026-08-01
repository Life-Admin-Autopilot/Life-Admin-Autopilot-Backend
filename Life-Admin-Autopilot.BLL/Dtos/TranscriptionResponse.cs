namespace Life_Admin_Autopilot.BLL.Dtos
{
    // NFR-8: when ASR is unavailable the system informs the user rather than failing
    // silently, so a failure is a normal return value here - never an exception.
    public class TranscriptionResponse
    {
        public bool Succeeded { get; init; }

        // The spoken command, ready to hand to the Planning Agent.
        public string Transcript { get; init; } = string.Empty;

        public string? DetectedLanguage { get; init; }

        public double? AudioDurationSeconds { get; init; }

        public long LatencyMs { get; init; }

        public string? ErrorCode { get; init; }

        // Safe to show the user: no provider internals, no stack traces.
        public string? ErrorMessage { get; init; }

        public static TranscriptionResponse Success(
            string transcript,
            string? detectedLanguage,
            double? audioDurationSeconds,
            long latencyMs) => new()
            {
                Succeeded = true,
                Transcript = transcript,
                DetectedLanguage = detectedLanguage,
                AudioDurationSeconds = audioDurationSeconds,
                LatencyMs = latencyMs
            };

        public static TranscriptionResponse Fail(string errorCode, string errorMessage) => new()
        {
            Succeeded = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}
