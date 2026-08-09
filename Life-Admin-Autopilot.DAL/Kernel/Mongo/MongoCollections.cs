namespace Life_Admin_Autopilot.DAL.Kernel.Mongo;

/// <summary>
/// Mongo collection names, exactly as Mongoose pluralises the model names in
/// <c>server/src/models/*.ts</c>. The parity database is written by BOTH servers
/// in testing, so a mis-pluralised name here silently produces an empty second
/// collection instead of an error.
///
/// A slice that owns a collection not listed here adds its own constant in its
/// own file — do NOT extend this class (it is a merge-conflict magnet).
/// </summary>
public static class MongoCollections
{
    public const string Users = "users";
    public const string Tasks = "tasks";
    public const string Clarifications = "clarifications";
    public const string Notifications = "notifications";
    public const string TaskBulkOps = "taskbulkops";
    public const string RefreshTokens = "refreshtokens";
    public const string VerificationTokens = "verificationtokens";
    public const string VoiceNotes = "voicenotes";
    public const string ScannedDocuments = "scanneddocuments";
    public const string AiConversations = "aiconversations";
    public const string DailyDigests = "dailydigests";
    public const string IcsFeeds = "icsfeeds";
    public const string Integrations = "integrations";

    // Usage counters — the three quota buckets.
    public const string AiUsageCounters = "aiusagecounters";
    public const string DocumentScanUsageCounters = "documentscanusagecounters";
    public const string TranslationUsageCounters = "translationusagecounters";
}
