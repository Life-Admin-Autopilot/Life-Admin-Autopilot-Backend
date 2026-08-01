using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Speech.Models.Internal
{
    // DeepInfra answers with two different error envelopes depending on which layer
    // rejected the request: FastAPI-style { "detail": "..." } for validation, and an
    // OpenAI-style { "error": { "message": ... } } for gateway-level rejections such as
    // 429. Both are parsed so the log line carries the real reason either way.
    internal class DeepInfraErrorResponse
    {
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("error")]
        public DeepInfraErrorDetail? Error { get; set; }
    }

    internal class DeepInfraErrorDetail
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}
