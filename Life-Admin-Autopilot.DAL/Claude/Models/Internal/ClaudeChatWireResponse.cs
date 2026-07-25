using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Claude.Models.Internal
{
    // Confirmed empirically against the real gateway (2026-07-25, model
    // meta.llama4-scout-17b-instruct-v1:0 - the mandated Anthropic model was blocked by
    // an account-level Bedrock use-case gate at the time, see Claude_Code_Brief_Stories_1_2).
    // Anthropic models are expected to return the same envelope once that gate clears.
    internal class ClaudeChatWireResponse
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("model_id")]
        public string? ModelId { get; set; }

        [JsonPropertyName("output_text")]
        public string? OutputText { get; set; }

        [JsonPropertyName("usage")]
        public ClaudeUsage? Usage { get; set; }

        [JsonPropertyName("estimated_cost_usd")]
        public string? EstimatedCostUsd { get; set; }

        [JsonPropertyName("actual_cost_usd")]
        public string? ActualCostUsd { get; set; }
    }

    internal class ClaudeUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }

        [JsonPropertyName("fallback_used")]
        public bool FallbackUsed { get; set; }
    }
}