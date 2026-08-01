using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Push.Models.Internal
{
    // FCM v1 acknowledges a send with nothing but the message name:
    // { "name": "projects/{projectId}/messages/{messageId}" }
    internal class FcmSendWireResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
