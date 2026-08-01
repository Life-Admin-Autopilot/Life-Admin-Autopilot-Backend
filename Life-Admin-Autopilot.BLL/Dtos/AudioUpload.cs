namespace Life_Admin_Autopilot.BLL.Dtos
{
    // Deliberately not IFormFile: that is a Presentation-layer type, and the Planning
    // Agent path will hand this service audio it did not receive over HTTP.
    public record AudioUpload(Stream Content, string FileName, string ContentType, long LengthBytes);
}
