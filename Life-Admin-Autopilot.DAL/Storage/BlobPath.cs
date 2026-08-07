namespace Life_Admin_Autopilot.DAL.Storage
{
    // What gets stored on documents.BlobUrl and users.ProfilePictureUrl is a path
    // ("documents/{userId}/{guid}.pdf"), never a SAS URL. A SAS expires, so persisting one
    // would leave every record pointing at a dead link and would put a live credential in
    // the database. Read URLs are minted from this path on demand instead.
    public static class BlobPath
    {
        // The user id leads the blob name so ownership is provable from the path alone -
        // a caller asking for someone else's file is caught before any network call.
        public static string Create(string userId, string originalFileName)
        {
            var extension = Path.GetExtension(originalFileName);

            return $"{userId}/{Guid.NewGuid():N}{extension}";
        }

        public static string Combine(string container, string blobName) => $"{container}/{blobName}";

        // Splits "documents/user-1/abc.pdf" into its container and the rest. Returns false
        // for anything malformed rather than throwing, because these strings come out of
        // the database and off the wire.
        public static bool TrySplit(string? path, out string container, out string blobName)
        {
            container = string.Empty;
            blobName = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var separatorIndex = path.IndexOf('/');
            if (separatorIndex <= 0 || separatorIndex == path.Length - 1)
            {
                return false;
            }

            container = path[..separatorIndex];
            blobName = path[(separatorIndex + 1)..];

            // "documents/../secrets" style traversal must never reach the storage client.
            return !blobName.Contains("..", StringComparison.Ordinal);
        }

        // Ownership is checked against the path rather than the database, so a caller
        // cannot read another user's document by guessing an id.
        public static bool IsOwnedBy(string blobName, string userId) =>
            blobName.StartsWith(userId + "/", StringComparison.Ordinal);
    }
}
