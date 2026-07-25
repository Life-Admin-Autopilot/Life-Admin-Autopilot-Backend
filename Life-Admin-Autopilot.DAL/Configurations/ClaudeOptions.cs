namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class ClaudeOptions
    {
        public const string SectionName = "Claude";

        // The gateway's own chat endpoint. Deliberately http, not https - that is the
        // gateway's real scheme (see Claude_Code_Brief_Stories_1_2), not a typo to fix.
        public string ChatEndpointUrl { get; set; } = "http://apiaccess.iti.net.eg/api/v1/student/chat";

        // Mandated by the brief - do not default this to the gateway docs' example model.
        public string ModelId { get; set; } = "anthropic.claude-haiku-4-5-20251001-v1:0";

        // Deliberately generous: this interface also backs AI Copilot Chat (RAG), whose
        // prompts are longer and more context-heavy than short task-extraction prompts.
        public int DefaultMaxTokens { get; set; } = 4096;

        public int TimeoutSeconds { get; set; } = 30;

        public int MaxRetryAttempts { get; set; } = 3;

        // Never set via appsettings.json - populated from the SBG_API_KEY configuration
        // key (env var in real deployments, user-secrets locally) by AddClaudeService.
        public string ApiKey { get; set; } = string.Empty;
    }
}