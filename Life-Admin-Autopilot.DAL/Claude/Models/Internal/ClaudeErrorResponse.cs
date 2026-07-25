using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Claude.Models.Internal
{
    // Confirmed empirically (twice, from two different gateway-level rejections):
    // { "error": { "code": "...", "message": "...", "details": { ... } } }
    internal class ClaudeErrorResponse
    {
        [JsonPropertyName("error")]
        public ClaudeErrorDetail? Error { get; set; }
    }

    internal class ClaudeErrorDetail
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}