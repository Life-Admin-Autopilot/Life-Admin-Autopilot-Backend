namespace Life_Admin_Autopilot.DAL.Speech.Models
{
    public class TranscriptionRequest
    {
        // Not buffered into a byte[]: the audio goes straight from the upload stream onto
        // the wire, so a large recording never sits in memory twice.
        public Stream Audio { get; set; } = Stream.Null;

        public string FileName { get; set; } = "audio.wav";

        public string ContentType { get; set; } = "audio/wav";

        // Overrides SpeechOptions.Language for this one call - used when the caller
        // already knows the user's locale and does not need auto-detection.
        public string? Language { get; set; }
    }
}
