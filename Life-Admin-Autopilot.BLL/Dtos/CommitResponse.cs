namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class CommitResponse
    {
        public bool Succeeded { get; set; }

        public string? TaskId { get; set; }

        public string? DocumentId { get; set; }

        // Where the document ended up after promotion, so the caller can stop referring
        // to the staging path.
        public string? DocumentPath { get; set; }

        // Whether the task was indexed for Copilot Chat. A commit is still a success when
        // this is false - the task is saved either way - but the caller is told, because
        // an unindexed task is invisible to search and that is not obvious.
        public bool Indexed { get; set; }

        public string? IndexWarning { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorMessage { get; set; }

        public static CommitResponse Fail(string code, string message) => new()
        {
            Succeeded = false,
            ErrorCode = code,
            ErrorMessage = message
        };
    }
}
