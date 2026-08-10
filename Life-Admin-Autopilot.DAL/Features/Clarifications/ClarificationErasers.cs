using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Clarifications;

/// <summary>
/// This slice owns the <c>clarifications</c> surface, so it owns the erasure —
/// Node's hand-maintained list in <c>routes/me.ts</c> includes
/// <c>Clarification.deleteMany({userId})</c>, and no other slice had registered it.
///
/// <para>
/// Nothing here holds blob storage, so <see cref="UserErasureOrder.Dependents"/> is
/// the right order: there is no cleanup that must happen before the rows go.
/// </para>
/// </summary>
public sealed class ClarificationEraser : MongoCollectionEraser
{
    public ClarificationEraser(IMongoDatabase database)
        : base("clarifications", MongoCollections.Clarifications) => UseDatabase(database);
}
