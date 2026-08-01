namespace Life_Admin_Autopilot.DAL.Push.Models
{
    public class PushNotificationRequest
    {
        // The FCM registration token for a single device. On iOS this is still an FCM
        // token, not the raw APNs token - Firebase swaps it for the APNs one internally.
        public string DeviceToken { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        // Delivered alongside the notification so the client can deep-link (e.g.
        // taskId). FCM only accepts string values here.
        public Dictionary<string, string>? Data { get; set; }
    }
}