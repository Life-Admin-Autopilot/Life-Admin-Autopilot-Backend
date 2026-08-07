using Azure.Storage.Blobs;

namespace Life_Admin_Autopilot.DAL.Storage
{
    // Holds the storage client, or nothing when no connection string is configured.
    // A nullable service cannot be registered in the container directly, and registering
    // it conditionally would make IFileStorageService unresolvable in environments without
    // storage - which would take the whole API down at startup rather than failing only
    // the calls that actually need a blob.
    public sealed class BlobClientProvider
    {
        public BlobClientProvider(BlobServiceClient? client) => Client = client;

        public BlobServiceClient? Client { get; }

        public bool IsConfigured => Client is not null;

        public static BlobClientProvider FromConnectionString(string? connectionString) =>
            new(string.IsNullOrWhiteSpace(connectionString) ? null : new BlobServiceClient(connectionString));
    }
}
