using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Push.Models.Internal
{
    // Matches FCM HTTP v1's POST /v1/projects/{projectId}/messages:send body. A single
    // message carries both the android and apns blocks; FCM applies whichever one matches
    // the platform the token came from, so there is no per-platform send path.
    internal class FcmSendWireRequest
    {
        [JsonPropertyName("message")]
        public FcmWireMessage Message { get; set; } = new();
    }

    internal class FcmWireMessage
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("notification")]
        public FcmWireNotification Notification { get; set; } = new();

        [JsonPropertyName("data")]
        public Dictionary<string, string>? Data { get; set; }

        [JsonPropertyName("android")]
        public FcmWireAndroidConfig? Android { get; set; }

        [JsonPropertyName("apns")]
        public FcmWireApnsConfig? Apns { get; set; }
    }

    internal class FcmWireNotification
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;
    }

    internal class FcmWireAndroidConfig
    {
        [JsonPropertyName("priority")]
        public string Priority { get; set; } = "high";

        [JsonPropertyName("notification")]
        public FcmWireAndroidNotification Notification { get; set; } = new();
    }

    internal class FcmWireAndroidNotification
    {
        // Must match a channel the client already created, otherwise Android 8+ shows the
        // notification silently under the app's default channel.
        [JsonPropertyName("channel_id")]
        public string ChannelId { get; set; } = string.Empty;

        [JsonPropertyName("sound")]
        public string Sound { get; set; } = "default";
    }

    internal class FcmWireApnsConfig
    {
        [JsonPropertyName("headers")]
        public Dictionary<string, string> Headers { get; set; } = new();

        [JsonPropertyName("payload")]
        public FcmWireApnsPayload Payload { get; set; } = new();
    }

    internal class FcmWireApnsPayload
    {
        [JsonPropertyName("aps")]
        public FcmWireAps Aps { get; set; } = new();
    }

    internal class FcmWireAps
    {
        [JsonPropertyName("alert")]
        public FcmWireApsAlert Alert { get; set; } = new();

        [JsonPropertyName("sound")]
        public string Sound { get; set; } = "default";
    }

    internal class FcmWireApsAlert
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;
    }
}
