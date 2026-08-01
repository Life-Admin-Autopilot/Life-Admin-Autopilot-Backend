namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class PushNotificationOptions
    {
        public const string SectionName = "PushNotifications";

        // Firebase project id (Project settings -> General). Left empty it is read from
        // the service account credential, which carries the same value.
        public string ProjectId { get; set; } = string.Empty;

        // FCM HTTP v1. The legacy https://fcm.googleapis.com/fcm/send endpoint is
        // decommissioned - do not "fix" this back to it.
        public string FcmBaseUrl { get; set; } = "https://fcm.googleapis.com/v1";

        // Android notification channel the client creates for reminders. Must match the
        // channel id registered in the Capacitor app or Android silently drops the sound.
        public string AndroidChannelId { get; set; } = "reminders";

        public int TimeoutSeconds { get; set; } = 30;

        public int MaxRetryAttempts { get; set; } = 3;

        // Never set via appsettings.json - populated by AddPushNotifications from the
        // FCM_SERVICE_ACCOUNT_JSON configuration key (env var in real deployments,
        // user-secrets locally).
        public string ServiceAccountJson { get; set; } = string.Empty;

        // Local-dev alternative to the above, from FCM_SERVICE_ACCOUNT_FILE. Ignored when
        // ServiceAccountJson is set. The file itself must stay out of source control.
        public string ServiceAccountFilePath { get; set; } = string.Empty;
    }
}