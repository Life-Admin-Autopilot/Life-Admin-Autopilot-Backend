using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.VoiceNotes;

/// <summary>
/// The one thing the voice slice does to the shared <c>notifications</c>
/// collection: write the "your voice note is ready" row once processing settles.
///
/// <para>
/// Port of <c>server/src/lib/voiceNoteNotification.ts</c>. Note what is NOT here:
/// unlike the document-scan path there is <b>no push-preference check</b>. Node's
/// voice notifier calls <c>Notification.create</c> unconditionally — the row is
/// the durable in-app record, and delivery to the OS is the client's job via
/// locally-scheduled notifications. Adding a <c>notifications.push</c> gate here
/// would silently drop feed rows the reference server writes.
/// </para>
/// </summary>
public interface IVoiceNoteNotifications
{
    Task CreateReadyAsync(
        ObjectId userId,
        int extractedCount,
        int clarifyCount,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVoiceNoteNotifications"/>
public sealed class VoiceNoteNotifications : IVoiceNoteNotifications
{
    /// <summary>
    /// <c>kind: 'reminder'</c> — NOT a voice-specific kind. The enum has no
    /// <c>voice_note</c> member, so the reference reuses <c>reminder</c>; inventing
    /// a new kind here would emit a value the client's switch does not handle.
    /// </summary>
    public const string Kind = "reminder";

    public const string Title = "Your voice note is ready";

    private readonly IMongoCollection<NotificationDocument> _notifications;

    public VoiceNoteNotifications(IMongoDatabase database)
    {
        _notifications = database.GetCollection<NotificationDocument>(MongoCollections.Notifications);
    }

    /// <summary>
    /// Copied verbatim, middle dot and all. Every lane produces a real task now, so
    /// the count the user cares about is how many things got FILED — not how many
    /// are waiting on them.
    /// </summary>
    public static string BodyFor(int extractedCount, int clarifyCount)
    {
        var filed = extractedCount + clarifyCount;
        if (filed == 0)
        {
            return "Nothing to file from that one.";
        }

        var head = filed == 1 ? "1 thing filed" : $"{filed} things filed";
        if (clarifyCount == 0)
        {
            return head;
        }

        var tail = clarifyCount == 1 ? "1 guess to confirm" : $"{clarifyCount} guesses to confirm";
        return $"{head} · {tail}";
    }

    public Task CreateReadyAsync(
        ObjectId userId,
        int extractedCount,
        int clarifyCount,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        return _notifications.InsertOneAsync(
            new NotificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                Kind = Kind,
                Title = Title,
                Body = BodyFor(extractedCount, clarifyCount),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken: cancellationToken);
    }
}
