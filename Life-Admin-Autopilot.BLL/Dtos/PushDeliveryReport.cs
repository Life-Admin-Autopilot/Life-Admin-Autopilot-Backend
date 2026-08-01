namespace Life_Admin_Autopilot.BLL.Dtos
{
    // A user can have several devices, so a send is a per-device outcome list rather than
    // a single success/failure - one dead phone must not hide a delivered reminder.
    public class PushDeliveryReport
    {
        public int SentCount { get; init; }

        public int FailedCount { get; init; }

        public IReadOnlyList<PushDeliveryResult> Results { get; init; } = Array.Empty<PushDeliveryResult>();

        // No registered device is a distinct state from a failed send: the reminder had
        // nowhere to go, which is a client-setup problem rather than a delivery problem.
        public bool HasRegisteredDevices => Results.Count > 0;

        public static PushDeliveryReport From(IReadOnlyList<PushDeliveryResult> results) => new()
        {
            SentCount = results.Count(result => result.Succeeded),
            FailedCount = results.Count(result => !result.Succeeded),
            Results = results
        };
    }

    public class PushDeliveryResult
    {
        // Masked - a full device token is a capability to push to that device and never
        // belongs in an API response or a log line.
        public string DeviceToken { get; init; } = string.Empty;

        public bool Succeeded { get; init; }

        public string? MessageId { get; init; }

        public string? ErrorCode { get; init; }

        public string? ErrorMessage { get; init; }

        // True when this send retired the token, so the caller can tell "delivery failed,
        // will retry later" apart from "this device is gone for good".
        public bool TokenDeactivated { get; init; }
    }
}
