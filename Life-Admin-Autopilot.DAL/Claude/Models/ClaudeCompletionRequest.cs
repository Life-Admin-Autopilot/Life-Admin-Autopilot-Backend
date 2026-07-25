namespace Life_Admin_Autopilot.DAL.Claude.Models
{
    public class ClaudeCompletionRequest
    {
        public IReadOnlyList<ClaudeMessage> Messages { get; init; } = Array.Empty<ClaudeMessage>();

        public string? SystemPrompt { get; init; }

        // Null falls back to ClaudeOptions.DefaultMaxTokens. Callers with long,
        // context-heavy prompts (e.g. Copilot RAG) can override this per call.
        public int? MaxTokens { get; init; }

        public static ClaudeCompletionRequest ForSingleMessage(string userText, string? systemPrompt = null, int? maxTokens = null) =>
            new()
            {
                Messages = new[] { new ClaudeMessage { Role = "user", Content = userText } },
                SystemPrompt = systemPrompt,
                MaxTokens = maxTokens
            };

        // For the Document Agent's file-upload flow (image/scanned-PDF field extraction).
        // Presence of images routes this through the gateway's multimodal-chat endpoint.
        public static ClaudeCompletionRequest ForImageExtraction(
            string prompt,
            IReadOnlyList<ClaudeImageAttachment> images,
            int? maxTokens = null) =>
            new()
            {
                Messages = new[] { new ClaudeMessage { Role = "user", Content = prompt, Images = images } },
                MaxTokens = maxTokens
            };
    }
}