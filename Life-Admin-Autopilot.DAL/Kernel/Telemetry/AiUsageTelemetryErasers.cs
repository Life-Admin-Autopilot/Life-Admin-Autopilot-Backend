using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Telemetry;

/// <summary>
/// The raw per-call events. Erased on account deletion — a row naming a user, the
/// conversation they had, and when they had it is personal data, whatever else it
/// is also useful for.
/// </summary>
public sealed class AiUsageEventEraser : MongoCollectionEraser
{
    public AiUsageEventEraser(IMongoDatabase database)
        : base("ai-usage-events", TelemetryCollections.AiUsageEvents) => UseDatabase(database);
}

/// <summary>
/// The per-user-per-day aggregates.
///
/// <para>
/// <b>Erased too, and that is a deliberate cost.</b> Deleting these makes historical
/// company-wide totals shrink retroactively — last March's spend chart will get
/// smaller every time someone deletes their account. The alternative is keeping a
/// per-user cost history for someone who asked to be forgotten, which is not a
/// defensible reading of the request. If aggregate history has to survive deletion,
/// the honest fix is a separate anonymous daily total with no user key, not
/// retaining these rows.
/// </para>
/// </summary>
public sealed class AiUsageRollupEraser : MongoCollectionEraser
{
    public AiUsageRollupEraser(IMongoDatabase database)
        : base("ai-usage-rollups", TelemetryCollections.AiUsageRollups) => UseDatabase(database);
}
