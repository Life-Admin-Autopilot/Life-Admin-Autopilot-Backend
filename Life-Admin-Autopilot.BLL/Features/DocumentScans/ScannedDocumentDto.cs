using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// One extracted candidate on the wire. Embedded with <c>_id: false</c> in
/// Mongoose, so unlike a subtask it has no id of its own and no transform.
/// </summary>
public sealed class ExtractedTaskCandidateDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "medium";

    [JsonPropertyName("estimate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskEstimateDto? Estimate { get; init; }

    /// <summary>What this one action costs. Absent whenever the page carried no figure for it.</summary>
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoneyDto? Amount { get; init; }

    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DueAt { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }

    [JsonPropertyName("sourcePage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourcePage { get; init; }

    [JsonPropertyName("taskId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }
}

/// <summary>
/// Wire shape of a scanned document — the output of
/// <c>ScannedDocumentSchema.toJSON</c>.
///
/// <para>
/// <b>Nine fields are absent by design</b>, and their absence is the contract:
/// <c>storageKey</c>, <c>rawExtractedText</c>, <c>attempts</c>,
/// <c>maxAttempts</c>, <c>manualRetries</c>, <c>lockedUntil</c>,
/// <c>nextRunAt</c>, <c>lastError</c> and <c>notifiedAt</c>. There is no property
/// for any of them here, so no future edit can leak one by accident.
/// </para>
///
/// <para>
/// <c>failureReason</c> deliberately SURVIVES the transform even though
/// <c>lastError</c> — which usually holds the same string — does not. The
/// difference is intent: one is a plain-language sentence the error screen shows
/// the user, the other is job machinery.
/// </para>
///
/// <para>
/// <b>Property order follows Mongoose's EMISSION order, not the schema.</b>
/// KERNEL.md §6 says "schema order"; that is a good heuristic and it is wrong
/// here. Mongoose serialises keys in the order the document first set them, so
/// the create-time fields come out in the order of the route's object literal
/// (<c>…status, clientCapturedAt, timezone</c>), the schema defaults
/// (<c>candidates</c>) follow, and anything the worker fills in afterwards —
/// <c>failureReason</c>, the four row-copy fields, <c>reviewedAt</c> — lands
/// AFTER <c>createdAt</c>/<c>updatedAt</c>. Confirmed by diffing this response
/// against the live reference byte for byte. <c>id</c> and the derived
/// <c>canRetry</c> stay last, as §6 says.
/// </para>
/// </summary>
public sealed class ScannedDocumentDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = "pdf";

    [JsonPropertyName("pageCount")]
    public int PageCount { get; init; }

    [JsonPropertyName("byteSize")]
    public int ByteSize { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    [JsonPropertyName("clientCapturedAt")]
    public DateTime ClientCapturedAt { get; init; }

    [JsonPropertyName("timezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timezone { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<ExtractedTaskCandidateDto> Candidates { get; init; } =
        Array.Empty<ExtractedTaskCandidateDto>();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    // ---- Everything the WORKER fills in later ------------------------------
    // Mongoose serialises a document's keys in the order they were first SET,
    // not in schema order, so a field the extraction pass adds after creation
    // lands AFTER createdAt/updatedAt. Verified live: a failed scan emits
    // `…,"updatedAt":…,"failureReason":…,"id":…,"canRetry":…`.

    [JsonPropertyName("failureReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; init; }

    [JsonPropertyName("documentSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentSummary { get; init; }

    [JsonPropertyName("documentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentType { get; init; }

    [JsonPropertyName("documentTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentTitle { get; init; }

    [JsonPropertyName("documentSubtitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentSubtitle { get; init; }

    [JsonPropertyName("issuer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Issuer { get; init; }

    /// <summary>
    /// The document's headline total. Written by the worker alongside the other
    /// row-copy fields, so it emits in this position for the same reason they do.
    /// </summary>
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoneyDto? Amount { get; init; }

    [JsonPropertyName("amountDueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? AmountDueAt { get; init; }

    [JsonPropertyName("reviewedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ReviewedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// DERIVED, never stored: <c>status == "failed" &amp;&amp; manualRetries &lt; 3</c>.
    /// The counter and the cap stay server-side; the client only ever learns
    /// whether the retry button should be on the screen.
    /// </summary>
    [JsonPropertyName("canRetry")]
    public bool CanRetry { get; init; }
}
