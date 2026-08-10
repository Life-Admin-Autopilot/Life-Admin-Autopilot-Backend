using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.DocumentScans;

/// <summary>
/// Closed vocabularies and budgets from <c>server/src/models/ScannedDocument.ts</c>.
/// </summary>
public static class ScannedDocumentVocabulary
{
    public static readonly IReadOnlyList<string> Statuses =
        new[] { "pending", "processing", "ready_for_review", "failed" };

    /// <summary>
    /// Claimable INCLUDES <c>processing</c> on purpose — a crash mid-extraction has
    /// to be recoverable once the lease expires, so the reclaim query must be able
    /// to see a row that is still nominally in flight.
    /// </summary>
    public static readonly IReadOnlyList<string> ClaimableStatuses = new[] { "pending", "processing" };

    public static readonly IReadOnlyList<string> Sources = new[] { "camera", "pdf", "gallery" };

    public static readonly IReadOnlyList<string> DocumentTypes = new[]
    {
        "bill", "statement", "letter", "form", "receipt",
        "insurance", "medical", "legal", "identity", "tax", "other",
    };

    /// <summary>
    /// How many times the USER may hand-restart extraction after a terminal
    /// failure. Lives beside the document rather than in the route because the
    /// derived <c>canRetry</c> flag and the gate the reprocess route enforces have
    /// to agree — one constant, so the button can never be offered for a retry the
    /// server will refuse.
    /// </summary>
    public const int MaxManualScanRetries = 3;

    public const int DocumentTitleMax = 60;
    public const int DocumentSubtitleMax = 120;
    public const int DocumentIssuerMax = 80;
}

/// <summary>
/// One extracted item held for review. Nothing from a scan auto-saves; every
/// candidate needs an explicit accept or discard, so <c>confidence</c> is
/// informational rather than a persistence gate.
/// </summary>
public sealed class ExtractedTaskCandidateDocument
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Priority { get; set; } = "normal";

    public string Confidence { get; set; } = "medium";

    public Kernel.Documents.TaskEstimateDocument? Estimate { get; set; }

    public DateTime? DueAt { get; set; }

    public string? Notes { get; set; }

    public int? SourcePage { get; set; }

    /// <summary>Set once the candidate has been accepted into a Task.</summary>
    public ObjectId? TaskId { get; set; }
}

/// <summary>
/// Port of <c>server/src/models/ScannedDocument.ts</c>.
///
/// <para>
/// Nine of these members NEVER reach a client — <c>StorageKey</c>,
/// <c>RawExtractedText</c>, <c>Attempts</c>, <c>MaxAttempts</c>,
/// <c>ManualRetries</c>, <c>LockedUntil</c>, <c>NextRunAt</c>, <c>LastError</c>
/// and <c>NotifiedAt</c> are all deleted by the Mongoose <c>toJSON</c> transform.
/// <c>FailureReason</c> SURVIVES it, which is the whole reason it exists
/// separately from <c>LastError</c>: one is plain-language product state the
/// error screen renders, the other is job machinery. Go through
/// <c>DocumentScanMappers.ToDto</c>, never a direct serialization.
/// </para>
/// </summary>
public sealed class ScannedDocumentDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>
    /// Mongoose stamps <c>__v: 0</c> on insert for every model except User, which
    /// is the only one setting <c>versionKey: false</c>
    /// (<c>server/src/models/User.ts:217</c>). The .NET driver adds nothing, so a
    /// document written here was missing a field the reference stores. Observable
    /// today through <c>GET /me/export</c>, which returns raw stored rows.
    /// </summary>
    [BsonElement("__v")]
    public int SchemaVersion { get; set; }

    public ObjectId UserId { get; set; }

    public string StorageKey { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public string SourceType { get; set; } = "pdf";

    public int PageCount { get; set; }

    /// <summary>
    /// <c>int</c>, not <c>long</c>: the Node BSON serializer writes a JS integer
    /// inside the int32 range as int32, and the ceiling here is 15 MiB. A C#
    /// <c>long</c> would store int64 and give the two servers different BSON types
    /// for the same logical value.
    /// </summary>
    public int ByteSize { get; set; }

    public string Status { get; set; } = "pending";

    public string? RawExtractedText { get; set; }

    /// <summary>
    /// Plain-language failure text. Present only while <c>Status == "failed"</c>,
    /// cleared by reprocess — and, unlike <see cref="LastError"/>, EXPOSED.
    /// </summary>
    public string? FailureReason { get; set; }

    public string? DocumentSummary { get; set; }

    public string? DocumentType { get; set; }

    public string? DocumentTitle { get; set; }

    public string? DocumentSubtitle { get; set; }

    public string? Issuer { get; set; }

    public List<ExtractedTaskCandidateDocument> Candidates { get; set; } = new();

    public DateTime ClientCapturedAt { get; set; }

    public string? Timezone { get; set; }

    // ---- Job machinery. None of it is ever sent to a client. ----------------

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// <see cref="Attempts"/>/<see cref="MaxAttempts"/> bound ONE automatic backoff
    /// ladder; this bounds how many fresh ladders the retry button can buy, so a
    /// permanently-stuck document cannot become an unbounded spend loop around the
    /// monthly quota.
    /// </summary>
    public int ManualRetries { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime NextRunAt { get; set; }

    public string? LastError { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime? NotifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
