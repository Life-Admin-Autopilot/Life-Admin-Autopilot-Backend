using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Notifications;

/// <summary>
/// The in-app feed. This slice owns the <c>notifications</c> collection, so it
/// owns the erasure — Node deletes the same rows in
/// <c>routes/me.ts</c>'s hand-maintained list
/// (<c>Notification.deleteMany({userId})</c>).
///
/// <para>
/// Notifications hold no blob storage, so <see cref="UserErasureOrder.Dependents"/>
/// is right; there is nothing to clean up ahead of the row itself.
/// </para>
/// </summary>
public sealed class NotificationEraser : MongoCollectionEraser
{
    public NotificationEraser(IMongoDatabase database)
        : base("notifications", MongoCollections.Notifications) => UseDatabase(database);
}
