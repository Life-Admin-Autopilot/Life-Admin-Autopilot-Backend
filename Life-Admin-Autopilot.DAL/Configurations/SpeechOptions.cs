namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class SpeechOptions
    {
        public const string SectionName = "Speech";

        // Hugging Face's Inference Providers router, on the fal-ai route. The model is not
        // served by hf-inference, deepinfra or together through the router - fal is the
        // only provider that answers, confirmed by probing all four.
        public string TranscriptionUrl { get; set; } =
            "https://router.huggingface.co/fal-ai/nvidia/nemotron-asr-multilingual/asr";

        // Logging and diagnostics only - the route above already pins the model.
        public string ModelId { get; set; } = "nvidia/nemotron-3.5-asr-streaming-0.6b";

        // "auto" is fine for English but collapses into Latin transliteration on Arabic
        // ("Fakarnia grad..."), so the client should always send the user's locale rather
        // than relying on detection. See LanguageNormalizer.
        public string DefaultLanguage { get; set; } = "auto";

        // A spoken command is seconds long, and the audio travels as base64 inside JSON,
        // which inflates it by a third.
        public long MaxAudioBytes { get; set; } = 5 * 1024 * 1024;

        // NFR-1 gives the whole voice-to-task chain 5 seconds and transcription is only
        // the first hop, but this call crosses a router to a third-party provider.
        public int TimeoutSeconds { get; set; } = 30;

        // Deliberately lower than the Claude/FCM services: a user is waiting on this call,
        // so a long retry chain costs more than it recovers.
        public int MaxRetryAttempts { get; set; } = 2;

        // Stereo 48kHz uploads transcribe identically to mono 16kHz ones, so the client is
        // not required to convert and the backend does no audio processing.
        public List<string> AllowedContentTypes { get; set; } = new()
        {
            "audio/wav",
            "audio/x-wav",
            "audio/wave",
            "audio/vnd.wave",
            "audio/mpeg",
            "audio/mp3"
        };

        // Never set via appsettings.json - populated from the HF_TOKEN configuration key
        // (env var in real deployments, user-secrets locally).
        public string ApiKey { get; set; } = string.Empty;
    }
}
