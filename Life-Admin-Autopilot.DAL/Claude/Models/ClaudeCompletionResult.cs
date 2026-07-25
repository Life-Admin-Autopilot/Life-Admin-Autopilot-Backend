namespace Life_Admin_Autopilot.DAL.Claude.Models
{
    public class ClaudeCompletionResult
    {
        public string CompletionText { get; init; } = string.Empty;

        public string ModelId { get; init; } = string.Empty;

        // True if the gateway substituted a different model/region than requested.
        // Worth checking before trusting a spike result as representative of the
        // intended model (see the "don't generalize to Claude broadly" note in the brief).
        public bool FallbackUsed { get; init; }

        public int InputTokens { get; init; }

        public int OutputTokens { get; init; }

        public int TotalTokens { get; init; }

        public string? StopReason { get; init; }

        public decimal? EstimatedCostUsd { get; init; }

        public decimal? ActualCostUsd { get; init; }

        public long LatencyMs { get; init; }

        public string RawResponseBody { get; init; } = string.Empty;
    }
}