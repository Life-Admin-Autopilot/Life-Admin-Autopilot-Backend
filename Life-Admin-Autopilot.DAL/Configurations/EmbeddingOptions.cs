namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class EmbeddingOptions
    {
        public const string SectionName = "Embedding";

        // Hugging Face rather than the ITI gateway: the gateway's catalogue carries 69
        // models and not one of them embeds - every embedding model id returns
        // POLICY_MODEL_NOT_APPROVED.
        public string BaseUrl { get; set; } = "https://router.huggingface.co";

        // bge-m3 is bilingual and 1024-dimensional, which is exactly what
        // content_chunks_vector_index expects, so no index rebuild is needed.
        // Measured on Egyptian task phrases against their English translations:
        // bge-m3 separated matched from unrelated pairs by 0.310, multilingual-e5-large
        // by only 0.087.
        public string ModelId { get; set; } = "BAAI/bge-m3";

        // Atlas rejects a vector of the wrong length, and a silently wrong-sized one
        // would be worse, so the service checks before writing.
        public int Dimensions { get; set; } = 1024;

        public int TimeoutSeconds { get; set; } = 60;

        public int MaxRetryAttempts { get; set; } = 2;

        // From HF_TOKEN. Never appsettings.json.
        public string ApiKey { get; set; } = string.Empty;
    }
}
