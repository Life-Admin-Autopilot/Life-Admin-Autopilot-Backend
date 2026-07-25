using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Claude.Models.Internal
{
    // Matches the gateway's confirmed "Multimodal chat" request shape
    // (/api/v1/student/multimodal-chat) - "text" + "images" per message, NOT the
    // text-only Chat shape's "content" field. Deliberately has no system_prompt field:
    // the docs example didn't show one for this endpoint, unlike text-only Chat.
    internal class ClaudeMultimodalWireRequest
    {
        [JsonPropertyName("model_id")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ClaudeMultimodalWireMessage> Messages { get; set; } = new();

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }
    }

    internal class ClaudeMultimodalWireMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("images")]
        public List<ClaudeWireImage>? Images { get; set; }
    }

    internal class ClaudeWireImage
    {
        [JsonPropertyName("format")]
        public string Format { get; set; } = "png";

        [JsonPropertyName("data_base64")]
        public string DataBase64 { get; set; } = string.Empty;
    }
}