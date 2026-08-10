using Life_Admin_Autopilot.BLL.Kernel.Dtos;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Kernel.Mappers;

/// <summary>
/// Every Mongoose <c>toJSON</c> transform, made explicit.
///
/// <para>
/// <b>NEVER serialize a <c>*Document</c> straight to the wire.</b> Mongoose
/// applies a per-schema transform on the way out — mapping <c>_id</c> to a string
/// <c>id</c>, deleting <c>__v</c>, deleting secrets and job machinery, and
/// DERIVING fields that are not stored at all (<c>hasPassword</c>,
/// <c>priorityRank</c>, <c>canRetry</c>). A direct serialization silently leaks
/// <c>passwordHash</c> / <c>storageKey</c> and omits fields the client indexes on.
/// This class is the only sanctioned way out.
/// </para>
///
/// <para>
/// A slice adding a new collection writes its own mapper in its own folder,
/// following the same rules. Read the <c>toJSON</c> block of the corresponding
/// <c>server/src/models/*.ts</c> first — the deletions are the whole point.
/// </para>
/// </summary>
public static class KernelMappers
{
    /// <summary>ObjectId to its 24-hex string, matching <c>String(ret._id)</c>.</summary>
    public static string ToId(this ObjectId id) => id.ToString();

    public static string? ToIdOrNull(this ObjectId? id) => id?.ToString();

    // ---- Task -------------------------------------------------------------

    public static TaskDto ToDto(this TaskDocument doc) => new()
    {
        UserId = doc.UserId.ToId(),
        Title = doc.Title,
        Domain = doc.Domain,
        Kind = doc.Kind,
        Status = doc.Status,
        Priority = doc.Priority,
        Subtasks = doc.Subtasks.Select(ToDto).ToList(),
        Tags = doc.Tags.ToList(),
        DueAt = doc.DueAt,
        Notes = doc.Notes,
        SourceLocale = doc.SourceLocale,
        I18n = doc.I18n?.ToDictionary(kv => kv.Key, kv => kv.Value.ToDto()),
        SourceVoiceNoteId = doc.SourceVoiceNoteId.ToIdOrNull(),
        SourceDocumentId = doc.SourceDocumentId.ToIdOrNull(),
        SourceTaskKey = doc.SourceTaskKey,
        ExternalSource = doc.ExternalSource,
        ExternalId = doc.ExternalId,
        TimePrecision = doc.TimePrecision,
        Confidence = doc.Confidence,
        Estimate = doc.Estimate is null
            ? null
            : new TaskEstimateDto
            {
                MinMinutes = doc.Estimate.MinMinutes,
                MaxMinutes = doc.Estimate.MaxMinutes,
                Source = doc.Estimate.Source,
            },
        CompletedAt = doc.CompletedAt,
        SnoozedUntil = doc.SnoozedUntil,
        Reminders = doc.Reminders
            .Select(r => new ReminderEntryDto { At = r.At, FiredAt = r.FiredAt, Kind = r.Kind })
            .ToList(),
        DeletedAt = doc.DeletedAt,
        RescheduleCount = doc.RescheduleCount,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),

        // Derived, not stored. PRIORITY_RANK lives in exactly one place.
        PriorityRank = TaskVocabulary.RankFor(doc.Priority),
    };

    /// <summary>
    /// Subtasks get their OWN transform in Mongoose because a parent schema's
    /// transform does not recurse into embedded subdocuments.
    /// </summary>
    public static SubtaskDto ToDto(this SubtaskDocument doc) => new()
    {
        Text = doc.Text,
        Done = doc.Done,
        CreatedAt = doc.CreatedAt,
        Id = doc.Id.ToId(),
    };

    public static TaskTranslationDto ToDto(this TaskTranslationDocument doc) => new()
    {
        Title = doc.Title,
        Notes = doc.Notes,
        Subtasks = doc.Subtasks is null ? null : new Dictionary<string, string>(doc.Subtasks),
        At = doc.At,
    };

    // ---- User -------------------------------------------------------------

    /// <summary>
    /// Drops <c>passwordHash</c> and derives <c>hasPassword</c> from it. There is
    /// no code path that emits the hash.
    /// </summary>
    public static UserDto ToDto(this UserProfileDocument doc) => new()
    {
        Email = doc.Email,
        PendingEmail = doc.PendingEmail,
        DisplayName = doc.DisplayName,
        HasOnboarded = doc.HasOnboarded,
        EmailVerifiedAt = doc.EmailVerifiedAt,
        Timezone = doc.Timezone,
        Locale = doc.Locale,
        LocaleFollowsDevice = doc.LocaleFollowsDevice,
        Theme = doc.Theme,
        TextSize = doc.TextSize,
        PreferredDomains = doc.PreferredDomains.ToList(),
        OnboardingAnswers = doc.OnboardingAnswers
            .Select(a => new OnboardingAnswerDto { Id = a.Id, Question = a.Question, Answer = a.Answer })
            .ToList(),
        Mic = new MicPrefsDto { Quality = doc.Mic.Quality },
        Notifications = new NotificationPrefsDto
        {
            Push = doc.Notifications.Push,
            EmailDigest = doc.Notifications.EmailDigest,
            Marketing = doc.Notifications.Marketing,
        },
        Imports = new ImportPrefsDto { DefaultTimeOfDay = doc.Imports.DefaultTimeOfDay },
        Privacy = new PrivacyPrefsDto
        {
            Analytics = doc.Privacy.Analytics,
            CrashReports = doc.Privacy.CrashReports,
        },
        Subscription = new SubscriptionStateDto
        {
            Tier = doc.Subscription.Tier,
            RenewsAt = doc.Subscription.RenewsAt,
            CanceledAt = doc.Subscription.CanceledAt,
        },
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),
        HasPassword = !string.IsNullOrEmpty(doc.PasswordHash),
    };

    // ---- Clarification ----------------------------------------------------

    /// <summary>Drops the internal <c>sourceKey</c> idempotency key.</summary>
    public static ClarificationDto ToDto(this ClarificationDocument doc) => new()
    {
        UserId = doc.UserId.ToId(),
        TaskId = doc.TaskId.ToIdOrNull(),
        Status = doc.Status,
        Draft = new ClarificationDraftDto
        {
            Title = doc.Draft.Title,
            Domain = doc.Draft.Domain,
            Priority = doc.Draft.Priority,
            Notes = doc.Draft.Notes,
            Tags = doc.Draft.Tags.ToList(),
            DueAt = doc.Draft.DueAt,
        },
        Question = doc.Question,
        Kind = doc.Kind,
        CostOfWrong = doc.CostOfWrong,
        Options = doc.Options
            .Select(o => new ClarificationOptionDto
            {
                Label = o.Label,
                DueAt = o.DueAt,
                Title = o.Title,
                Notes = o.Notes,
            })
            .ToList(),
        SourceText = doc.SourceText,
        DeferredUntil = doc.DeferredUntil,
        Answer = doc.Answer,
        ResolvedAt = doc.ResolvedAt,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),
    };

    // ---- Notification -----------------------------------------------------

    public static NotificationDto ToDto(this NotificationDocument doc) => new()
    {
        UserId = doc.UserId.ToId(),
        Kind = doc.Kind,
        Title = doc.Title,
        Body = doc.Body,
        TaskId = doc.TaskId.ToIdOrNull(),
        ClarificationId = doc.ClarificationId.ToIdOrNull(),
        DocumentId = doc.DocumentId.ToIdOrNull(),
        ReadAt = doc.ReadAt,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),
    };

    // ---- TaskBulkOp -------------------------------------------------------

    public static TaskBulkOpDto ToDto(this TaskBulkOpDocument doc) => new()
    {
        UserId = doc.UserId.ToId(),
        Kind = doc.Kind,
        Action = doc.Action,
        Status = doc.Status,
        Entries = doc.Entries
            .Select(e => new TaskBulkOpEntryDto
            {
                TaskId = e.TaskId.ToId(),
                Confidence = e.Confidence,
                Reason = e.Reason,
            })
            .ToList(),
        Label = doc.Label,
        AppliedAt = doc.AppliedAt,
        UndoneAt = doc.UndoneAt,
        ExpiresAt = doc.ExpiresAt,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),
    };
}
