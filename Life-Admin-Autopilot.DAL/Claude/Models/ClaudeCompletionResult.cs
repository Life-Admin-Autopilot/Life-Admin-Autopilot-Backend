namespace Life_Admin_Autopilot.DAL.Claude.Models
{
    public class ClaudeCompletionResult
    {
        public string CompletionText { get; init; } = string.Empty;

        public string ModelId { get; init; } = string.Empty;

        // TEMPORARY: the gateway's real success response shape has never been observed
        // (every test call so far hit a model-approval/policy error, not a 2xx). This is
        // included so the first successful call can be inspected end-to-end and
        // ClaudeService's response parsing hardened into a precise DTO. Remove once the
        // real shape is confirmed and parsing no longer needs a fallback.
        public string RawResponseBody { get; init; } = string.Empty;
    }
}