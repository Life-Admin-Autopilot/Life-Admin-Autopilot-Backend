using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>A clash between a matter and something the user already has.</summary>
public sealed record MatterConflict(ObjectId TaskId, string Title, DateTime? DueAt, string Kind, string Reason)
{
    public const string TimeClash = "time_clash";
    public const string Duplicate = "duplicate";
}

/// <summary>
/// Conflict detection, shared by both agents.
///
/// <para>
/// The Planning Agent runs it on a DRAFT before anything is saved; the Knowledge
/// Agent re-runs it on an EXISTING task after an edit. Same rules, two moments —
/// which is exactly how <c>ai_flow_V4</c> splits them, and why this lives on its own
/// rather than inside either caller.
/// </para>
/// </summary>
public sealed class ConflictService
{
    /// <summary>Two dated matters this close together are flagged as clashing.</summary>
    public static readonly TimeSpan ClashWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Cosine similarity above which two matters are the same thing. 0.92 is the
    /// threshold <c>docs/ai-stories.md</c> already specifies for duplicate tasks.
    /// </summary>
    public const double DuplicateThreshold = 0.92;

    private readonly TaskRepository _tasks;
    private readonly KnowledgeService _knowledge;
    private readonly ILogger<ConflictService> _logger;

    public ConflictService(
        TaskRepository tasks,
        KnowledgeService knowledge,
        ILogger<ConflictService> logger)
    {
        _tasks = tasks;
        _knowledge = knowledge;
        _logger = logger;
    }

    /// <summary>
    /// Does this instant collide with anything in the pool?
    ///
    /// <para>
    /// The time half of <see cref="CheckAsync"/>, pulled out so a candidate slot
    /// can be tested without an embedding round trip. Suggestions call it once
    /// per candidate, and they MUST agree with the check that runs at save —
    /// which is why this is the same comparison rather than a second one.
    /// </para>
    /// </summary>
    public static bool ClashesWithin(DateTime at, IReadOnlyList<TaskDocument> pool, ObjectId? excludeTaskId)
    {
        foreach (var task in pool)
        {
            if (excludeTaskId is not null && task.Id == excludeTaskId) continue;
            if (task.DueAt is not { } other) continue;
            if ((other - at).Duration() <= ClashWindow) return true;
        }

        return false;
    }

    /// <summary>Every open matter for this user — the pool both checks run against.</summary>
    public Task<List<TaskDocument>> OpenMattersAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        _tasks.Tasks
            .Find(Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
                Builders<TaskDocument>.Filter.Eq(t => t.Status, "open")))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Check one candidate against the pool.
    ///
    /// <para>
    /// <paramref name="excludeTaskId"/> is what makes the re-check usable on a SAVED
    /// task: without it every edited task conflicts with itself, at similarity 1.0
    /// and zero minutes apart, and the user is warned about a duplicate of the thing
    /// they are editing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MatterConflict>> CheckAsync(
        ObjectId userId,
        string title,
        DateTime? dueAt,
        IReadOnlyList<TaskDocument> pool,
        ObjectId? excludeTaskId = null,
        CancellationToken cancellationToken = default)
    {
        var conflicts = new List<MatterConflict>();
        var candidates = pool.Where(t => excludeTaskId is null || t.Id != excludeTaskId).ToList();

        if (dueAt is { } due)
        {
            foreach (var task in candidates)
            {
                if (task.DueAt is not { } other) continue;
                if ((other - due).Duration() > ClashWindow) continue;

                conflicts.Add(new MatterConflict(
                    task.Id,
                    task.Title,
                    task.DueAt,
                    MatterConflict.TimeClash,
                    "Scheduled within two hours of this."));
            }
        }

        var duplicate = await DuplicateAsync(userId, title, candidates, excludeTaskId, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate is not null) conflicts.Add(duplicate);

        return conflicts;
    }

    /// <summary>
    /// Near-duplicate over the embedded corpus. Best-effort: with retrieval
    /// unconfigured the caller still gets its time-clash answer.
    /// </summary>
    private async Task<MatterConflict?> DuplicateAsync(
        ObjectId userId,
        string title,
        IReadOnlyList<TaskDocument> candidates,
        ObjectId? excludeTaskId,
        CancellationToken cancellationToken)
    {
        if (!_knowledge.IsConfigured || string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            var matches = await _knowledge
                .SearchAsync(userId, title, 5, cancellationToken)
                .ConfigureAwait(false);

            foreach (var match in matches)
            {
                if (match.Score < DuplicateThreshold) continue;
                if (excludeTaskId is not null && match.Chunk.SourceId == excludeTaskId) continue;

                // Chunks outlive their task (a delete need not have swept them yet),
                // so only report a duplicate we can still point the user at.
                var existing = candidates.FirstOrDefault(t => t.Id == match.Chunk.SourceId);
                if (existing is null) continue;

                return new MatterConflict(
                    existing.Id,
                    existing.Title,
                    existing.DueAt,
                    MatterConflict.Duplicate,
                    $"Looks like a duplicate of an existing matter ({match.Score:0.00} similar).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "conflict:duplicate-check-failed");
        }

        return null;
    }
}
