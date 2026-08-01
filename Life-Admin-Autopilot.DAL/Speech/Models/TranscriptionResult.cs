namespace Life_Admin_Autopilot.DAL.Speech.Models
{
    public class TranscriptionResult
    {
        // What the user said. This is the Planning Agent's input.
        public string Text { get; set; } = string.Empty;

        // Null on the Voxtral route, which does not report a detected language. Kept on
        // the model because it is provider-dependent, not because it is always available.
        public string? DetectedLanguage { get; set; }

        // Length of the audio as the provider measured it, when it reports one.
        public double? AudioDurationSeconds { get; set; }

        public long LatencyMs { get; set; }

        // Prompt tokens scale with audio length, so this is the practical signal for how
        // close a recording is to the model's context limit.
        public int? PromptTokens { get; set; }

        public int? CompletionTokens { get; set; }
    }
}
