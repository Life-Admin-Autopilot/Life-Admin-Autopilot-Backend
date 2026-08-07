namespace Life_Admin_Autopilot.DAL.Storage.Models
{
    // Stable, provider-agnostic error codes surfaced on Result.Error.Code, so callers can
    // react without knowing anything about Azure.
    public static class StorageErrorCodes
    {
        public const string NoFile = "STORAGE_NO_FILE";

        public const string FileTooLarge = "STORAGE_FILE_TOO_LARGE";

        public const string UnsupportedFormat = "STORAGE_UNSUPPORTED_FORMAT";

        // The blob is not where the database says it is - a document record pointing at a
        // deleted or expired staged file, most likely.
        public const string NotFound = "STORAGE_NOT_FOUND";

        // The account key is wrong, expired, or has been rotated out from under us.
        public const string NotAuthorized = "STORAGE_NOT_AUTHORIZED";

        public const string NotConfigured = "STORAGE_NOT_CONFIGURED";

        public const string Unavailable = "STORAGE_UNAVAILABLE";

        // Caller passed a path that does not belong to the user asking for it. Treated as
        // an error rather than an empty result so it is visible in the logs.
        public const string AccessDenied = "STORAGE_ACCESS_DENIED";

        public static bool IsClientError(string errorCode) =>
            errorCode is NoFile or FileTooLarge or UnsupportedFormat or NotFound or AccessDenied;
    }
}
