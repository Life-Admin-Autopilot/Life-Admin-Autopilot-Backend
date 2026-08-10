using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>Every 2xx body the document-scan routes emit. One class per envelope.</summary>
public sealed class ScanSingleResponse
{
    [JsonPropertyName("scannedDocument")]
    public ScannedDocumentDto ScannedDocument { get; init; } = new();
}

public sealed class ScanListResponse
{
    [JsonPropertyName("scannedDocuments")]
    public IReadOnlyList<ScannedDocumentDto> ScannedDocuments { get; init; } =
        Array.Empty<ScannedDocumentDto>();
}

/// <summary>
/// The monthly meter. <c>kind</c> is a literal, not the bucket discriminator —
/// this counter has no discriminator at all (one row per user per month).
/// </summary>
public sealed class DocumentScanQuotaDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "document_scan";

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("used")]
    public int Used { get; init; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; init; }

    /// <summary>
    /// Midnight UTC on the 1st of the following month, as a string rather than a
    /// <c>DateTime</c> — Node builds it with <c>toISOString()</c> and ships the
    /// string, so it never goes through date binding on either side.
    /// </summary>
    [JsonPropertyName("resetAt")]
    public string ResetAt { get; init; } = string.Empty;
}

public sealed class ScanQuotaResponse
{
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "free";

    [JsonPropertyName("quota")]
    public DocumentScanQuotaDto Quota { get; init; } = new();
}

/// <summary>
/// The review commit's body: only the Tasks THIS call created, plus the updated
/// document.
/// </summary>
public sealed class ScanReviewResponse
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskDto> Tasks { get; init; } = Array.Empty<TaskDto>();

    [JsonPropertyName("scannedDocument")]
    public ScannedDocumentDto ScannedDocument { get; init; } = new();
}

/// <summary>
/// The 402 payload. Deliberately a DIFFERENT shape from the AI quota's
/// <c>{kind,tier,limit,used,resetAt}</c> — this one carries no <c>kind</c> and no
/// <c>resetAt</c>, and a shared "quota error" type would quietly unify the two.
/// </summary>
public sealed class DocumentScanQuotaExceededDetails
{
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "free";

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("used")]
    public int Used { get; init; }
}
