namespace Life_Admin_Autopilot.BLL.Dtos
{
    // A rejected upload is a normal return value, not an exception - same contract as
    // TranscriptionResponse, so the API layer maps both the same way.
    public class FileUploadResponse
    {
        public bool Succeeded { get; init; }

        // What gets persisted: "documents-staging/{userId}/{guid}.pdf". Deliberately not a
        // URL - see BlobPath for why storing a SAS would be a mistake.
        public string? Path { get; init; }

        // Short-lived URL the client can actually fetch. Minted per response, expires.
        public string? ReadUrl { get; init; }

        public string? ContentType { get; init; }

        public long SizeBytes { get; init; }

        public string? ErrorCode { get; init; }

        public string? ErrorMessage { get; init; }

        public static FileUploadResponse Success(string path, string? readUrl, string contentType, long sizeBytes) => new()
        {
            Succeeded = true,
            Path = path,
            ReadUrl = readUrl,
            ContentType = contentType,
            SizeBytes = sizeBytes
        };

        public static FileUploadResponse Fail(string errorCode, string errorMessage) => new()
        {
            Succeeded = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}
