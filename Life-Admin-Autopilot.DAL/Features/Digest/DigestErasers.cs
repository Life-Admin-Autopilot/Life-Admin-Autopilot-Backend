using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Digest;

/// <summary>
/// The cached digests. <c>DailyDigest.deleteMany({userId})</c>.
///
/// <para>
/// Registered at the default <see cref="UserErasureOrder.Dependents"/> order: the
/// rows reference no blob storage and nothing else references them, so they can go
/// whenever. Slice K consumes the registry — this is how the collection joins the
/// account-deletion cascade without anyone editing a shared list.
/// </para>
/// </summary>
public sealed class DailyDigestEraser : MongoCollectionEraser
{
    public DailyDigestEraser(IMongoDatabase database)
        : base("daily-digests", DigestCollections.DailyDigests) => UseDatabase(database);
}
