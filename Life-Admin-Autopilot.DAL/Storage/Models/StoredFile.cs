namespace Life_Admin_Autopilot.DAL.Storage.Models
{
    public class StoredFile
    {
        // "documents/{userId}/{guid}.pdf" - what gets persisted on the document or user
        // record. Not a URL, and not directly usable by a client.
        public string Path { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        // Kept so the UI can show the user what they uploaded; the stored blob name is a
        // guid, which is deliberately meaningless.
        public string OriginalFileName { get; set; } = string.Empty;
    }

    public class DownloadedFile
    {
        // Buffered rather than streamed because the only caller feeds it to Claude as
        // base64 for extraction (FR-3.2), which needs the whole thing in memory anyway.
        public byte[] Content { get; set; } = Array.Empty<byte>();

        public string ContentType { get; set; } = string.Empty;
    }

    public class FileUpload
    {
        public Stream Content { get; set; } = Stream.Null;

        public string FileName { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public long LengthBytes { get; set; }
    }
}
