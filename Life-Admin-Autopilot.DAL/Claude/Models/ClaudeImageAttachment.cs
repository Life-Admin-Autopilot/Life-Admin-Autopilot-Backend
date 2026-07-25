namespace Life_Admin_Autopilot.DAL.Claude.Models
{
    public class ClaudeImageAttachment
    {
        public string Format { get; init; } = "png";

        public string DataBase64 { get; init; } = string.Empty;
    }
}