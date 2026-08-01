namespace Life_Admin_Autopilot.DAL.Speech.Models
{
    public class TranscriptionResult
    {
        // What the user said. This is the Planning Agent's input.
        public string Text { get; set; } = string.Empty;

        // Present when the model reports it - with Language set to "auto" this is how the
        // caller learns whether the user spoke English or Arabic.
        public string? DetectedLanguage { get; set; }

        // Length of the audio as the provider measured it, not of our request.
        public double? AudioDurationSeconds { get; set; }

        // Provider-side inference time, when reported. Useful next to LatencyMs: a big gap
        // between them is queueing or network, not model time.
        public long? InferenceRuntimeMs { get; set; }

        public decimal? CostUsd { get; set; }

        public long LatencyMs { get; set; }

        // Kept for the Planning Agent's benefit: per-segment text lets it split a long
        // command without re-running inference. Empty when the provider returns none.
        public IReadOnlyList<TranscriptionSegment> Segments { get; set; } = Array.Empty<TranscriptionSegment>();
    }

    public class TranscriptionSegment
    {
        public double StartSeconds { get; set; }

        public double EndSeconds { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}
