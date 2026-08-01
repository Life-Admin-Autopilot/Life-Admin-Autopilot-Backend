namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class SpeechOptions
    {
        public const string SectionName = "Speech";

        // DeepInfra's native inference API. The model id is appended to this base, so
        // switching models is a configuration change, not a code change.
        public string InferenceBaseUrl { get; set; } = "https://api.deepinfra.com/v1/inference";

        // Nemotron ASR, chosen over Azure Speech by the team. Streaming-capable and
        // multilingual, which matters because the app supports English and Arabic.
        public string ModelId { get; set; } = "nvidia/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b";

        // "auto" lets the model detect the language; a two-letter ISO 639-1 code (en, ar)
        // pins it. Auto is the default so an Arabic command is not forced into English.
        public string Language { get; set; } = "auto";

        // NFR-1 gives the whole voice-to-task chain 5 seconds, and transcription is only
        // the first hop - a request still running after this is worth abandoning.
        public int TimeoutSeconds { get; set; } = 15;

        // Deliberately lower than the Claude/FCM services: a user is waiting on this call,
        // so a long retry chain costs more than it recovers.
        public int MaxRetryAttempts { get; set; } = 2;

        // A spoken command is seconds long. This is a sanity ceiling to reject junk
        // uploads before they cost an inference call, not a real recording limit.
        public long MaxAudioBytes { get; set; } = 10 * 1024 * 1024;

        // The model card requires mono WAV; MP3 is accepted by the endpoint and kept here
        // for flexibility. Anything else is rejected before it reaches the provider.
        public List<string> AllowedContentTypes { get; set; } = new()
        {
            "audio/wav",
            "audio/x-wav",
            "audio/wave",
            "audio/vnd.wave",
            "audio/mpeg",
            "audio/mp3"
        };

        // Never set via appsettings.json - populated from the DEEPINFRA_TOKEN
        // configuration key (env var in real deployments, user-secrets locally).
        public string ApiKey { get; set; } = string.Empty;
    }
}
