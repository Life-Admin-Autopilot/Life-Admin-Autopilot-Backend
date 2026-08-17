using Life_Admin_Autopilot.BLL.Kernel.Dtos;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// The <c>toJSON</c> transform from <c>models/ScannedDocument.ts</c>, made
/// explicit. This is the only sanctioned way a scan reaches the wire.
///
/// <para>
/// The transform's whole job is its DELETIONS. Nine fields go — storage
/// location, raw OCR text and the seven job-machinery members — and one field is
/// invented, <c>canRetry</c>. Reading this mapper next to
/// <see cref="ScannedDocumentDto"/> should make both obvious.
/// </para>
/// </summary>
public static class DocumentScanMappers
{
    public static ScannedDocumentDto ToDto(this ScannedDocumentDocument doc) => new()
    {
        UserId = doc.UserId.ToId(),
        MimeType = doc.MimeType,
        SourceType = doc.SourceType,
        PageCount = doc.PageCount,
        ByteSize = doc.ByteSize,
        Status = doc.Status,
        FailureReason = doc.FailureReason,
        DocumentSummary = doc.DocumentSummary,
        DocumentType = doc.DocumentType,
        DocumentTitle = doc.DocumentTitle,
        DocumentSubtitle = doc.DocumentSubtitle,
        Issuer = doc.Issuer,
        Amount = doc.Amount.ToDto(),
        AmountDueAt = doc.AmountDueAt,
        Candidates = doc.Candidates.Select(ToDto).ToList(),
        ClientCapturedAt = doc.ClientCapturedAt,
        Timezone = doc.Timezone,
        ReviewedAt = doc.ReviewedAt,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),

        // Derived here rather than stored, so the counter and the cap can never
        // disagree with the gate POST /:id/reprocess enforces.
        CanRetry = doc.Status == "failed"
                   && doc.ManualRetries < ScannedDocumentVocabulary.MaxManualScanRetries,
    };

    public static ExtractedTaskCandidateDto ToDto(this ExtractedTaskCandidateDocument doc) => new()
    {
        Key = doc.Key,
        Title = doc.Title,
        Domain = doc.Domain,
        Priority = doc.Priority,
        Confidence = doc.Confidence,
        Estimate = doc.Estimate is null
            ? null
            : new TaskEstimateDto
            {
                MinMinutes = doc.Estimate.MinMinutes,
                MaxMinutes = doc.Estimate.MaxMinutes,
                Source = doc.Estimate.Source,
            },
        Amount = doc.Amount.ToDto(),
        DueAt = doc.DueAt,
        Notes = doc.Notes,
        SourcePage = doc.SourcePage,
        TaskId = doc.TaskId.ToIdOrNull(),
    };
}
