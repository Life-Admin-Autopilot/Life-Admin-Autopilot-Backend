using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Claude.Models
{
    public class ClaudeMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        // Presence of any images routes this request through the gateway's separate
        // multimodal-chat endpoint/shape instead of the text-only Chat one - see
        // ClaudeService. Never serialized directly from this type (the multimodal wire
        // shape uses "text", not "content").
        [JsonIgnore]
        public IReadOnlyList<ClaudeImageAttachment>? Images { get; init; }
    }
}