using Life_Admin_Autopilot.BLL.Kernel.Dtos;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// The <c>toJSON</c> transform from <c>models/VoiceNote.ts</c>, made explicit.
/// This is the only sanctioned way a note reaches the wire.
///
/// <para>
/// The transform's whole job is its DELETIONS — <c>storageKey</c>, the six
/// job-machinery members, and <c>clarifyItems</c>. Reading this mapper next to
/// <see cref="VoiceNoteDto"/> should make all eight obvious. Unlike the scan
/// transform this one derives NOTHING: there is no <c>canRetry</c> equivalent,
/// because a voice note has no manual-retry route.
/// </para>
/// </summary>
public static class VoiceNoteMappers
{
    public static VoiceNoteDto ToDto(this VoiceNoteDocument note) => new()
    {
        UserId = note.UserId.ToId(),
        DurationMs = note.DurationMs,
        ByteSize = note.ByteSize,
        Source = note.Source,
        Status = note.Status,
        ClientCapturedAt = note.ClientCapturedAt,
        Timezone = note.Timezone,
        MimeType = note.MimeType,
        ExtractedTasks = note.ExtractedTasks.Select(ToDto).ToList(),
        ReviewItems = note.ReviewItems.Select(ToDto).ToList(),
        CreatedAt = note.CreatedAt,
        UpdatedAt = note.UpdatedAt,
        Transcript = note.Transcript,
        FailureReason = note.FailureReason,
        ReviewedAt = note.ReviewedAt,
        Id = note.Id.ToId(),
    };

    public static VoiceExtractedTaskDto ToDto(this VoiceExtractedTaskDocument item) => new()
    {
        Key = item.Key,
        Title = item.Title,
        Domain = item.Domain,
        Priority = item.Priority,
        Confidence = item.Confidence,
        ReviewReason = item.ReviewReason,
        Estimate = ToDto(item.Estimate),
        DueAt = item.DueAt,
        Notes = item.Notes,
        TaskId = item.TaskId.ToIdOrNull(),
    };

    public static VoiceReviewItemDto ToDto(this VoiceReviewItemDocument item) => new()
    {
        Key = item.Key,
        Title = item.Title,
        Domain = item.Domain,
        Priority = item.Priority,
        Confidence = item.Confidence,
        ReviewReason = item.ReviewReason,
        Reasons = item.Reasons.ToList(),
        Estimate = ToDto(item.Estimate),
        DueRaw = item.DueRaw,
        DueAt = item.DueAt,
        Notes = item.Notes,
    };

    private static TaskEstimateDto? ToDto(Life_Admin_Autopilot.DAL.Kernel.Documents.TaskEstimateDocument? estimate) =>
        estimate is null
            ? null
            : new TaskEstimateDto
            {
                MinMinutes = estimate.MinMinutes,
                MaxMinutes = estimate.MaxMinutes,
                Source = estimate.Source,
            };
}
