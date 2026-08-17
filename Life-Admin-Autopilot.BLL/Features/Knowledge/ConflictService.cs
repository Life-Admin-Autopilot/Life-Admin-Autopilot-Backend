using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>
/// A clash between a matter and something the user already has.
///
/// <para>
/// <b>The identifying fields describe the OTHER matter</b> — the thing already in the
/// list that the candidate ran into. <see cref="Urgency"/> is the candidate's own
/// score and <see cref="OtherUrgency"/> belongs to the matter named here, which is
/// the pairing <see cref="Yields"/> is decided from.
/// </para>
/// </summary>
/// <param name="Urgency">
/// The candidate's urgency, scored at the moment of the check. Zero on a duplicate,
/// where nothing about time is in question.
/// </param>
/// <param name="Yields">
/// True when the CANDIDATE is the one that should move.
///
/// <para>
/// The spec's §3.3 requirement, and the point of scoring at all: reporting that two
/// matters collide leaves the user to work out which one they cared about, which is
/// the decision they were already failing to make when they double-booked. The lower
/// urgency gives way. On a tie the candidate yields, because the matter already in
/// the list is the one the user has previously committed to.
/// </para>
/// </param>
public sealed record MatterConflict(
    ObjectId TaskId,
    string Title,
    DateTime? DueAt,
    string Kind,
    string Reason,
    double Urgency = 0,
    double OtherUrgency = 0,
    bool Yields = false)
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
    /// <summary>
    /// How far a clashing matter is offered to move. A UI affordance, not the
    /// detection rule — see <see cref="MatterWindow.SuggestedShift"/>.
    /// </summary>
    public static TimeSpan SuggestedShift => MatterWindow.SuggestedShift;

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
    public static bool ClashesWithin(
        DateTime at,
        MatterCandidate candidate,
        IReadOnlyList<TaskDocument> pool,
        ObjectId? excludeTaskId)
    {
        // The candidate's OWN span moves with the instant being tested — a
        // four-hour job proposed for 14:00 occupies 10:00-14:00, and a suggester
        // that only knew the instant would offer slots it then refuses.
        var mine = MatterWindow.For(candidate.Title, candidate.Domain, candidate.Estimate, at);

        foreach (var task in pool)
        {
            if (excludeTaskId is not null && task.Id == excludeTaskId) continue;
            if (MatterWindow.For(task) is not { } theirs) continue;
            if (MatterWindow.Overlap(mine, theirs)) return true;
        }

        return false;
    }

    /// <summary>
    /// The facts about a matter that conflict detection needs, whether or not it has
    /// been saved yet.
    ///
    /// <para>
    /// <c>Domain</c> and <c>Estimate</c> resolve how long it occupies;
    /// <c>Priority</c> decides which side yields. A draft has all three before it is
    /// written, which is what lets the same check run at propose time and at save.
    /// </para>
    /// </summary>
    public readonly record struct MatterCandidate(
        string Title,
        string Domain,
        string Priority,
        TaskEstimateDocument? Estimate = null)
    {
        public static MatterCandidate From(TaskDocument task) =>
            new(task.Title, task.Domain, task.Priority, task.Estimate);
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
        MatterCandidate candidate,
        DateTime? dueAt,
        IReadOnlyList<TaskDocument> pool,
        ObjectId? excludeTaskId = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var at = now ?? DateTime.UtcNow;
        var conflicts = new List<MatterConflict>();
        var candidates = pool.Where(t => excludeTaskId is null || t.Id != excludeTaskId).ToList();

        if (dueAt is { } due)
        {
            var mine = MatterWindow.For(candidate.Title, candidate.Domain, candidate.Estimate, due);

            // Both sides are scored at the SAME instant, and at NOW rather than at
            // either deadline. Scored at its own deadline every matter reads as
            // maximally pressing, which is exactly the comparison that carries no
            // information; scored at today, a matter due tomorrow properly outranks
            // the same matter due in three weeks.
            var myUrgency = UrgencyOf(candidate.Title, candidate.Domain, candidate.Priority, due, at);

            foreach (var task in candidates)
            {
                if (MatterWindow.For(task) is not { } theirs) continue;
                if (!MatterWindow.Overlap(mine, theirs)) continue;

                var theirUrgency = UrgencyOf(
                    task.Title, task.Domain, task.Priority, task.DueAt!.Value, at);

                // A tie leaves the incumbent alone: it is the commitment the user has
                // already made, and moving it needs a reason better than "equal".
                var yields = myUrgency <= theirUrgency;

                conflicts.Add(new MatterConflict(
                    task.Id,
                    task.Title,
                    task.DueAt,
                    MatterConflict.TimeClash,
                    ClashReason(task.Title, yields),
                    myUrgency,
                    theirUrgency,
                    yields));
            }
        }

        var duplicate = await DuplicateAsync(
                userId, candidate.Title, candidates, excludeTaskId, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate is not null) conflicts.Add(duplicate);

        return conflicts;
    }

    private static double UrgencyOf(string title, string domain, string priority, DateTime dueAt, DateTime at) =>
        ReminderUrgency.Score(new ReminderTaskShape(title, domain, "reminder", dueAt), priority, at);

    /// <summary>
    /// Says which of the two should move, not merely that a problem exists. Naming
    /// the other matter is what makes the sentence actionable — "this clashes with
    /// something" is a fact the user cannot do anything with.
    /// </summary>
    private static string ClashReason(string otherTitle, bool yields) =>
        yields
            ? $"Overlaps \"{otherTitle}\", the more pressing of the two — better to move this one."
            : $"Overlaps \"{otherTitle}\", and this is the more pressing of the two.";

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
