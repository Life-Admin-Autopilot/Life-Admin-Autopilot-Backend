using System.Text.Json;
using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Speech.Models.Internal
{
    // The provider takes the audio as a data URI rather than a file upload or a raw body,
    // confirmed by probing the router: multipart and raw-bytes variants are rejected.
    internal class NemotronWireRequest
    {
        [JsonPropertyName("audio_url")]
        public string AudioUrl { get; set; } = string.Empty;

        [JsonPropertyName("language")]
        public string Language { get; set; } = "auto";
    }

    // {"output":"Renew my passport next Friday.","partial":false}
    // Note this is fal's own response shape, not the {"text": ...} that Hugging Face's
    // task documentation describes - the router passes the provider's body straight back.
    internal class NemotronWireResponse
    {
        [JsonPropertyName("output")]
        public string? Output { get; set; }

        // True when the provider returned an incomplete transcript, e.g. a streaming
        // response that was cut short.
        [JsonPropertyName("partial")]
        public bool Partial { get; set; }
    }

    // The router and the provider reject requests in two different shapes:
    //   {"error":"Model not supported by provider hf-inference"}
    //   {"detail":[{"type":"literal_error","loc":["body","language"],"msg":"..."}]}
    // so both are read, with "detail" as raw JSON because it arrives as a string, an
    // object or a validation array depending on which layer refused.
    internal class HuggingFaceErrorResponse
    {
        [JsonPropertyName("error")]
        public JsonElement? Error { get; set; }

        [JsonPropertyName("detail")]
        public JsonElement? Detail { get; set; }

        public string? Describe() => Read(Error) ?? Read(Detail);

        private static string? Read(JsonElement? element) => element switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            { } value => value.GetRawText()
        };
    }
}
