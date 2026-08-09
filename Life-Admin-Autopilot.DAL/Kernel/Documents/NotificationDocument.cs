using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

public static class NotificationVocabulary
{
    public static readonly IReadOnlyList<string> Kinds = new[] { "reminder", "uncertainty", "document_scan" };
}

/// <summary>
/// Port of <c>server/src/models/Notification.ts</c> — the in-app feed, and also
/// the push-ready queue.
/// </summary>
public sealed class NotificationDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public ObjectId? TaskId { get; set; }

    public ObjectId? ClarificationId { get; set; }

    public ObjectId? DocumentId { get; set; }

    public DateTime? ReadAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
