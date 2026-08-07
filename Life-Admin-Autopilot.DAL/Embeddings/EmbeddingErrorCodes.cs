namespace Life_Admin_Autopilot.DAL.Embeddings
{
    public static class EmbeddingErrorCodes
    {
        public const string NotConfigured = "EMBED_NOT_CONFIGURED";
        public const string EmptyText = "EMBED_EMPTY_TEXT";
        public const string Timeout = "EMBED_TIMEOUT";
        public const string RateLimited = "EMBED_RATE_LIMITED";
        public const string QuotaExceeded = "EMBED_QUOTA_EXCEEDED";
        public const string NetworkError = "EMBED_NETWORK_ERROR";
        public const string BadResponse = "EMBED_BAD_RESPONSE";

        // The vector came back the wrong length. Writing it would be rejected by Atlas,
        // and if the index were ever resized it would quietly poison search instead.
        public const string WrongDimensions = "EMBED_WRONG_DIMENSIONS";
    }
}
