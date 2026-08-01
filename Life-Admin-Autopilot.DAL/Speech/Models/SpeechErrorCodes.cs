namespace Life_Admin_Autopilot.DAL.Speech.Models
{
    // Stable, provider-agnostic error codes. NFR-8 requires the system to degrade
    // gracefully and tell the user what happened when ASR is unavailable, so each code
    // maps to a distinct thing the caller can say to the user.
    public static class SpeechErrorCodes
    {
        // The caller's fault and worth surfacing directly: nothing was uploaded, the file
        // is too big, or it is not an audio format the model accepts.
        public const string NoAudio = "ASR_NO_AUDIO";
        public const string AudioTooLarge = "ASR_AUDIO_TOO_LARGE";
        public const string UnsupportedFormat = "ASR_UNSUPPORTED_FORMAT";

        // The provider ran and heard nothing - silence, or a recording that never captured
        // the microphone. Distinct from a failure: the call worked, there is just no
        // command to plan from, so the user is asked to speak again.
        public const string EmptyTranscript = "ASR_EMPTY_TRANSCRIPT";

        // The provider rejected the audio itself (unreadable, corrupt, wrong container).
        public const string InvalidAudio = "ASR_INVALID_AUDIO";

        public const string NotAuthorized = "ASR_NOT_AUTHORIZED";

        public const string RateLimited = "ASR_RATE_LIMITED";

        public const string Unavailable = "ASR_UNAVAILABLE";

        // The provider did not answer in time. Kept separate from NetworkError because a
        // timeout is retryable and usually means the audio was long or the model is busy.
        public const string Timeout = "ASR_TIMEOUT";

        public const string NetworkError = "ASR_NETWORK_ERROR";

        public const string NotConfigured = "ASR_NOT_CONFIGURED";

        public const string UnrecognizedResponseShape = "ASR_UNRECOGNIZED_RESPONSE_SHAPE";

        public const string GatewayError = "ASR_GATEWAY_ERROR";

        // Bad input from the caller rather than a provider fault - the API layer answers
        // these with 4xx, everything else with 5xx.
        public static bool IsClientError(string errorCode) =>
            errorCode is NoAudio or AudioTooLarge or UnsupportedFormat or EmptyTranscript or InvalidAudio;
    }
}
