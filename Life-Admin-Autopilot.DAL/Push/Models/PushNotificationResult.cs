namespace Life_Admin_Autopilot.DAL.Push.Models
{
    public class PushNotificationResult
    {
        // FCM's message name, e.g. projects/{projectId}/messages/0:1500415314455276%31bd1c96.
        // Worth keeping in logs: it is the only handle support can correlate against.
        public string MessageId { get; set; } = string.Empty;

        public long LatencyMs { get; set; }
    }
}