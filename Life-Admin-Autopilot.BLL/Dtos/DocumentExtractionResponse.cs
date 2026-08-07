namespace Life_Admin_Autopilot.BLL.Dtos
{
    // What the Document Agent hands the Planning Agent: a plain-language account of the
    // file, so a task can be drafted from what the document actually says rather than
    // from its filename (SRS FR-3.2, and the third input source in story #30).
    public class DocumentExtractionResponse
    {
        public bool Succeeded { get; set; }

        // One or two sentences a person would recognise: what the document is, who it is
        // from, what it asks of the reader.
        public string? Description { get; set; }

        // The date the document itself states, when it states one. Left null rather than
        // guessed - a wrong due date on a bill is worse than no due date, because it
        // silently schedules the reminder for the wrong day.
        public string? DueDate { get; set; }

        public string? Amount { get; set; }

        public string? Issuer { get; set; }

        // Financial / Vehicle / Home / Health / Work-University / Personal / General.
        public string? Category { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorMessage { get; set; }

        public static DocumentExtractionResponse Fail(string code, string message) => new()
        {
            Succeeded = false,
            ErrorCode = code,
            ErrorMessage = message
        };
    }
}
