using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.DocumentScans;

/// <summary>
/// The two things the document-scan slice does to the shared
/// <c>notifications</c> collection: raise "scan ready to review", and clear the
/// notification when the scan it points at is deleted.
///
/// <para>
/// Narrow on purpose. The collection belongs to the notifications domain; this
/// type exists so those two writes are named and greppable rather than scattered
/// as raw driver calls inside an endpoint and a worker.
/// </para>
/// </summary>
public interface IDocumentScanNotifications
{
    /// <summary>
    /// <c>Notification.deleteMany({userId, documentId})</c>. Called after the scan
    /// row is gone — a notification pointing at a deleted document is a dead link
    /// in the feed.
    /// </summary>
    Task DeleteForDocumentAsync(
        ObjectId userId,
        ObjectId documentId,
        CancellationToken cancellationToken = default);

    /// <summary>True when the user has not turned push off.</summary>
    Task<bool> WantsPushAsync(ObjectId userId, CancellationToken cancellationToken = default);

    Task CreateReadyAsync(
        ObjectId userId,
        ObjectId documentId,
        int candidateCount,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDocumentScanNotifications"/>
public sealed class DocumentScanNotifications : IDocumentScanNotifications
{
    private readonly IMongoCollection<NotificationDocument> _notifications;
    private readonly IMongoCollection<UserProfileDocument> _users;

    public DocumentScanNotifications(IMongoDatabase database)
    {
        _notifications = database.GetCollection<NotificationDocument>(MongoCollections.Notifications);
        _users = database.GetCollection<UserProfileDocument>(MongoCollections.Users);
    }

    public Task DeleteForDocumentAsync(
        ObjectId userId,
        ObjectId documentId,
        CancellationToken cancellationToken = default) =>
        _notifications.DeleteManyAsync(
            Builders<NotificationDocument>.Filter.And(
                Builders<NotificationDocument>.Filter.Eq(n => n.UserId, userId),
                Builders<NotificationDocument>.Filter.Eq(n => n.DocumentId, documentId)),
            cancellationToken);

    /// <summary>
    /// Node's test is <c>user.notifications?.push !== false</c>: a MISSING user, or
    /// a missing prefs sub-document, still gets the notification. Only an explicit
    /// <c>false</c> suppresses it — except that a missing user suppresses it too,
    /// because the whole branch is guarded by <c>if (user &amp;&amp; …)</c>.
    /// </summary>
    public async Task<bool> WantsPushAsync(ObjectId userId, CancellationToken cancellationToken = default)
    {
        var user = await _users
            .Find(Builders<UserProfileDocument>.Filter.Eq(u => u.Id, userId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return user is not null && user.Notifications.Push;
    }

    public Task CreateReadyAsync(
        ObjectId userId,
        ObjectId documentId,
        int candidateCount,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        return _notifications.InsertOneAsync(
            new NotificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                Kind = "document_scan",
                DocumentId = documentId,
                Title = "Scan ready to review",
                Body = BodyFor(candidateCount),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Three literal strings, copied verbatim including the em dash.</summary>
    public static string BodyFor(int count) => count switch
    {
        0 => "We didn't find anything actionable in that scan.",
        1 => "1 item found — take a look.",
        _ => $"{count} items found — take a look.",
    };
}
