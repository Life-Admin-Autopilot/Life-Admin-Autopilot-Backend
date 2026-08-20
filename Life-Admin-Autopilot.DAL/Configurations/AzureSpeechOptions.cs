namespace Life_Admin_Autopilot.DAL.Configurations
{
    // Azure AI Speech, on the FAST TRANSCRIPTION route. Deliberately its own options class
    // rather than more keys on SpeechOptions: that one already mixes provider-specific
    // settings (TranscriptionUrl, ModelId) with provider-neutral ones (MaxAudioBytes), and
    // a second provider sharing the same bag would make it impossible to tell which key
    // belongs to whom.
    public class AzureSpeechOptions
    {
        public const string SectionName = "Speech:Azure";

        // Origin only - protocol and host, no path and no trailing slash. The custom
        // subdomain form the fast-transcription how-to documents:
        //   https://{resource-name}.cognitiveservices.azure.com
        // The regional form (https://{region}.api.cognitive.microsoft.com) also works,
        // which is why the whole origin is configured rather than just a region name.
        public string Endpoint { get; set; } = string.Empty;

        // Fast transcription is not on the classic /stt/... route. It answers at
        // /speechtotext/transcriptions:transcribe and REQUIRES an explicit api-version.
        public string ApiVersion { get; set; } = "2025-10-15";

        /// <summary>
        /// Candidate locales for language identification, used when the caller asks for
        /// "auto".
        ///
        /// <para>
        /// NOT an empty array. An empty <c>locales</c> selects Azure's multilingual model,
        /// whose supported set does not include Arabic at all - so the one setting that
        /// looks like "detect anything" would silently break the product's primary
        /// non-English language.
        /// </para>
        ///
        /// <para>
        /// At most ONE locale per base language: the language-identification docs give
        /// en-US together with en-GB as the explicit counter-example. en-US is chosen over
        /// the frontend's en-GB default because it is what the backend already normalises
        /// English onto, and continuity is worth more here than the accent model.
        /// </para>
        ///
        /// <para>
        /// A <c>string[]</c> rather than a <c>List&lt;string&gt;</c> on purpose: the
        /// configuration binder APPENDS to a pre-populated list and REPLACES an array, so
        /// a list default could only ever be widened from config, never narrowed.
        /// </para>
        /// </summary>
        public string[] AutoDetectLocales { get; set; } = ["en-US", "ar-EG"];

        // Lower than the Hugging Face route's 30s: fast transcription is synchronous and
        // advertised as faster than real time, and this provider now shares a wall-clock
        // budget with another one (SpeechOptions.TotalBudgetSeconds).
        public int TimeoutSeconds { get; set; } = 15;

        // One retry, not two. With a fallback in front of it the retry budget multiplies
        // across providers, and the user is still waiting.
        public int MaxRetryAttempts { get; set; } = 1;

        // Never set via appsettings.json - populated from the AZURE_SPEECH_KEY
        // configuration key (env var in real deployments, user-secrets locally). Anything
        // written here in appsettings is overwritten unconditionally at startup.
        public string ApiKey { get; set; } = string.Empty;

        // Both halves are required: a key without an endpoint has nowhere to go, and an
        // endpoint without a key is a guaranteed 401. Either missing means this provider
        // reports NotConfigured and costs nothing.
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Endpoint);
    }
}
