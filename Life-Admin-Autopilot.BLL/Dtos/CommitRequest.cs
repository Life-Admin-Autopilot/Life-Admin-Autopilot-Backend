namespace Life_Admin_Autopilot.BLL.Dtos
{
    // The payload the Planning Agent sends when the user confirms a draft. The shape is
    // fixed by what the Langflow Save Task tool already sends: a task, and optionally the
    // document that was staged alongside it.
    public class CommitRequest
    {
        public CommitTask Task { get; set; } = new();

        // Null when the user just spoke or typed a task with no file attached.
        public CommitDocument? Document { get; set; }
    }

    public class CommitTask
    {
        // Accepted but ignored: the owner always comes from the bearer token, so a caller
        // cannot write a task into someone else's list (NFR-5).
        public string? UserId { get; set; }

        public string Title { get; set; } = string.Empty;

        public DateTime? DueDate { get; set; }

        public string? Category { get; set; }

        public string? Priority { get; set; }

        public string? SourceType { get; set; }

        public string? Status { get; set; }
    }

    public class CommitDocument
    {
        // The staging path returned by POST /api/documents/staging. It is promoted into
        // the permanent container here, so the saved record does not point at a blob the
        // lifecycle rule is about to delete.
        public string BlobUrl { get; set; } = string.Empty;

        public string? Category { get; set; }

        // "pdf" or "photo".
        public string? SourceType { get; set; }

        public DateTime? UploadedAt { get; set; }

        public DateTime? ExpiryDate { get; set; }
    }
}
