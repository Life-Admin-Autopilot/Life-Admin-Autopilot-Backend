using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// The chat history. Registered at <see cref="UserErasureOrder.Dependents"/> — the
/// conversation holds no blob storage, so it needs no <c>Storage</c>-order pass.
///
/// <para>
/// Node keeps one hand-maintained collection list inside <c>routes/me.ts</c>; the
/// kernel inverts that into this registry precisely so the slice that OWNS a
/// collection is the one that remembers it. Slice K reads the registry — it must
/// never carry a hardcoded list.
/// </para>
/// </summary>
public sealed class AiConversationEraser : MongoCollectionEraser
{
    public AiConversationEraser(IMongoDatabase database)
        : base("ai-conversations", MongoCollections.AiConversations) => UseDatabase(database);
}

/// <summary>
/// The daily AI message counters. Erased so a re-registered account does not
/// inherit a spent day.
/// </summary>
public sealed class AiUsageEraser : MongoCollectionEraser
{
    public AiUsageEraser(IMongoDatabase database)
        : base("ai-usage", MongoCollections.AiUsageCounters) => UseDatabase(database);
}
