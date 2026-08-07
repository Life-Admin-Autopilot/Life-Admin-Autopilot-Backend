namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class ExtractDocumentRequest
    {
        // A staging path returned by POST /api/documents/staging. Ownership is checked
        // against the caller, so holding a path is not enough to read the file.
        public string Path { get; set; } = string.Empty;
    }
}
