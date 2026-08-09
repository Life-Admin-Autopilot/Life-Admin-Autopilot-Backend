using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Tasks;

/// <summary>
/// Matters themselves. Registered at <see cref="UserErasureOrder.Dependents"/> —
/// tasks hold no blob storage, so they need no <c>Storage</c>-order pass.
/// </summary>
public sealed class TaskEraser : MongoCollectionEraser
{
    public TaskEraser(IMongoDatabase database)
        : base("tasks", MongoCollections.Tasks) => UseDatabase(database);
}

/// <summary>
/// The undo journal. Erased alongside the tasks it describes — an op record that
/// outlived its rows would offer an undo that restores nothing.
/// </summary>
public sealed class TaskBulkOpEraser : MongoCollectionEraser
{
    public TaskBulkOpEraser(IMongoDatabase database)
        : base("task-bulk-ops", MongoCollections.TaskBulkOps) => UseDatabase(database);
}

/// <summary>
/// The per-language translate allowance. Erased so a re-registered account does
/// not inherit a spent month.
/// </summary>
public sealed class TranslationUsageEraser : MongoCollectionEraser
{
    public TranslationUsageEraser(IMongoDatabase database)
        : base("translation-usage", MongoCollections.TranslationUsageCounters) => UseDatabase(database);
}
