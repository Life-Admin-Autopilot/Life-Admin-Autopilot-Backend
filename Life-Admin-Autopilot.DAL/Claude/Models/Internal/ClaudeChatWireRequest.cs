using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Claude.Models.Internal
{
    // Matches the gateway's confirmed text-only "Chat" request shape
    // (/api/v1/student/chat) - NOT Anthropic's or OpenAI's native format.
    internal class ClaudeChatWireRequest
    {
        [JsonPropertyName("model_id")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ClaudeMessage> Messages { get; set; } = new();

        [JsonPropertyName("system_prompt")]
        public string? SystemPrompt { get; set; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }
    }
}