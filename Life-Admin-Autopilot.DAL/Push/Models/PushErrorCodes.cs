namespace Life_Admin_Autopilot.DAL.Push.Models
{
    // Stable, transport-agnostic error codes surfaced on Result.Error.Code so callers can
    // react (retire a token, back off, alert) without parsing FCM's own vocabulary.
    public static class PushErrorCodes
    {
        // FCM is certain this token can never receive again: the app was uninstalled, the
        // token rotated, or it belongs to a different Firebase sender. Safe to retire.
        public const string TokenInvalid = "PUSH_TOKEN_INVALID";

        // FCM rejected the request itself. Deliberately NOT TokenInvalid: a malformed
        // payload on our side would otherwise retire every user's device in one run.
        public const string InvalidArgument = "PUSH_INVALID_ARGUMENT";

        // Our service account is wrong/expired, or APNs rejected Firebase's credentials.
        // Nothing will be delivered to anyone until an operator fixes it.
        public const string NotAuthorized = "PUSH_NOT_AUTHORIZED";

        public const string RateLimited = "PUSH_RATE_LIMITED";

        public const string Unavailable = "PUSH_UNAVAILABLE";

        public const string NetworkError = "PUSH_NETWORK_ERROR";

        // No service account was supplied, so pushes are impossible in this environment.
        public const string NotConfigured = "PUSH_NOT_CONFIGURED";

        public const string UnrecognizedResponseShape = "PUSH_UNRECOGNIZED_RESPONSE_SHAPE";

        public const string GatewayError = "PUSH_GATEWAY_ERROR";

        // Only these mean "this device is gone" - see the comment on InvalidArgument for
        // why a rejected request does not qualify.
        public static bool IsTokenPermanentlyInvalid(string errorCode) =>
            errorCode == TokenInvalid;
    }
}
