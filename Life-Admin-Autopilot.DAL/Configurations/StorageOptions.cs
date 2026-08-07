namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class StorageOptions
    {
        public const string SectionName = "Storage";

        // Committed documents - written only when the user confirms a proposal, so a blob
        // here always has a matching documents record in Mongo.
        public string DocumentsContainer { get; set; } = "documents";

        // Uploaded during /planning/propose so Claude can read the file and the user can
        // preview it, before anything is saved. A blob lifecycle rule on the container
        // deletes anything still here after a day, which covers abandoned proposals.
        public string StagingContainer { get; set; } = "documents-staging";

        public string AvatarsContainer { get; set; } = "avatars";

        // Read URLs are minted on demand and deliberately short-lived. They only need to
        // outlive the screen that displays the document.
        public int ReadUrlLifetimeMinutes { get; set; } = 15;

        public long MaxDocumentBytes { get; set; } = 20 * 1024 * 1024;

        public long MaxAvatarBytes { get; set; } = 5 * 1024 * 1024;

        // Claude reads these directly for extraction (FR-3.2), so the list is what its
        // multimodal API accepts, not everything a user might try to upload.
        public List<string> AllowedDocumentContentTypes { get; set; } = new()
        {
            "application/pdf",
            "image/jpeg",
            "image/jpg",
            "image/png",
            "image/webp",
            "image/gif"
        };

        public List<string> AllowedAvatarContentTypes { get; set; } = new()
        {
            "image/jpeg",
            "image/jpg",
            "image/png",
            "image/webp"
        };

        // Never set via appsettings.json - populated from the
        // AZURE_STORAGE_CONNECTION_STRING configuration key (env var in real deployments,
        // user-secrets locally). It is a full-access account key.
        public string ConnectionString { get; set; } = string.Empty;
    }
}
