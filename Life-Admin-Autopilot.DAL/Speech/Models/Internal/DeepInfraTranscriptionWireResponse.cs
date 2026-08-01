using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Speech.Models.Internal
{
    // DeepInfra's native inference response for speech models:
    // { "text": "...", "segments": [...], "language": "en",
    //   "inference_status": { "status": "succeeded", "runtime_ms": 412, "cost": 0.00002 } }
    // Every field except text is treated as optional - the shape varies by model and a
    // missing extra must not cost us a transcript we successfully received.
    internal class DeepInfraTranscriptionWireResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("segments")]
        public List<DeepInfraWireSegment>? Segments { get; set; }

        [JsonPropertyName("inference_status")]
        public DeepInfraWireInferenceStatus? InferenceStatus { get; set; }
    }

    internal class DeepInfraWireSegment
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    internal class DeepInfraWireInferenceStatus
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("runtime_ms")]
        public long? RuntimeMs { get; set; }

        [JsonPropertyName("cost")]
        public decimal? Cost { get; set; }
    }
}
