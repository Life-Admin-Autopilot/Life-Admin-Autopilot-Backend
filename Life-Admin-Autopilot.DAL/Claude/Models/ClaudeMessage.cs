using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Claude.Models
{
    public class ClaudeMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}