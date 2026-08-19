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
        public int TimeoutSeconds { get; set; } = 20;

        // Deliberately lower than the Claude/FCM services: a user is waiting on this call,
        // so a long retry chain costs more than it recovers. Lowered from 2 when the Azure
        // fallback landed - the retry budget now multiplies across providers.
        public int MaxRetryAttempts { get; set; } = 1;

        /// <summary>
        /// Which provider is tried first: <c>"nemotron"</c> or <c>"azure"</c>.
        ///
        /// <para>
        /// Worth flipping rather than leaving alone. If the Hugging Face account's included
        /// credits are spent, "fallback" means every request pays a wasted call and its
        /// full timeout discovering that before it reaches the provider that works.
        /// <see cref="Speech.ProviderHealth"/> caps that at one wasted call an hour, but
        /// naming the provider that actually serves is the honest configuration.
        /// </para>
        /// </summary>
        public string PrimaryProvider { get; set; } = "nemotron";

        /// <summary>
        /// Wall-clock ceiling for ONE transcription across every provider and every retry
        /// underneath them.
        ///
        /// <para>
        /// The real control on how long a user waits. Retry counts alone cannot bound it:
        /// two providers, three attempts each at thirty seconds is three minutes for one
        /// recording, and four times that on the voice-note worker path. Still well over
        /// NFR-1's five seconds, but bounded and roughly a quarter of what an untuned
        /// failover chain would cost.
        /// </para>
        /// </summary>
        public int TotalBudgetSeconds { get; set; } = 25;

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
