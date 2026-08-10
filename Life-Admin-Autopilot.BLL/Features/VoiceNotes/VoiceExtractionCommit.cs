using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// Everything that happens to a note once extraction has produced three lanes:
/// stage the lanes, create Tasks for the confident ones, hold the questions, and
/// recompute the status.
///
/// <para>
/// Shared because BOTH the worker and <c>POST /me/voice-notes/{id}/extract-tasks</c>
/// do exactly this, and Node's two copies drifted once already — the route used to
/// destructure only <c>{autoSave, review}</c> and silently dropped the clarify
/// lane, so a manual re-extract lost every answerable question the worker path
/// would have held. One implementation is how that stays fixed.
/// </para>
/// </summary>
public interface IVoiceExtractionCommit
{
    /// <summary>
    /// Mutates <paramref name="note"/> in place and returns the Tasks created (or
    /// matched) for the auto-save lane, in lane order. Does NOT save the note — the
    /// caller owns the write, because the worker clears its lock in the same save
    /// and the route does not.
    /// </summary>
    Task<IReadOnlyList<TaskDocument>> ApplyAsync(
        VoiceNoteDocument note,
        VoiceGateResult gate,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVoiceExtractionCommit"/>
public sealed class VoiceExtractionCommit : IVoiceExtractionCommit
{
    private readonly IVoiceNoteTaskPersistence _tasks;
    private readonly IVoiceClarificationStaging _clarifications;

    public VoiceExtractionCommit(IVoiceNoteTaskPersistence tasks, IVoiceClarificationStaging clarifications)
    {
        _tasks = tasks;
        _clarifications = clarifications;
    }

    public async Task<IReadOnlyList<TaskDocument>> ApplyAsync(
        VoiceNoteDocument note,
        VoiceGateResult gate,
        CancellationToken cancellationToken = default)
    {
        note.ExtractedTasks = gate.AutoSave.Select(ToExtractedTask).ToList();
        note.ReviewItems = gate.Review.Select(ToReviewItem).ToList();
        note.ClarifyItems = gate.Clarify.Select(ToClarifyItem).ToList();

        var created = await _tasks
            .PersistAsync(note.UserId, note.Id, note.ExtractedTasks.Select(ToSeed).ToList(), cancellationToken)
            .ConfigureAwait(false);

        Backlink(note, created);

        // Hold the answerable items as Clarifications (home banner → /clarify
        // slider). Idempotent per (user, item key) so a reclaim never duplicates.
        await _clarifications.PersistAsync(note, cancellationToken).ConfigureAwait(false);

        note.Status = note.ReviewItems.Count > 0 ? "needs_review" : "ready";

        return created;
    }

    /// <summary>Back-link each staged record to the Task it produced.</summary>
    public static void Backlink(VoiceNoteDocument note, IReadOnlyList<TaskDocument> created)
    {
        if (created.Count == 0)
        {
            return;
        }

        var idByKey = created
            .Where(t => t.SourceTaskKey is not null)
            .GroupBy(t => t.SourceTaskKey!)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        foreach (var record in note.ExtractedTasks)
        {
            record.TaskId = idByKey.TryGetValue(record.Key, out var taskId) ? taskId : record.TaskId;
        }
    }

    public static VoiceTaskSeed ToSeed(VoiceExtractedTaskDocument record) => new(
        record.Key,
        record.Title,
        record.Domain,
        record.Priority,
        record.Confidence,
        record.Estimate,
        record.DueAt,
        record.Notes);

    private static VoiceExtractedTaskDocument ToExtractedTask(KeyedVoiceItem keyed) => new()
    {
        Key = keyed.Key,
        Title = keyed.Item.Title,
        Domain = keyed.Item.Domain,
        Priority = keyed.Item.Priority,
        Confidence = keyed.Item.Confidence,
        ReviewReason = keyed.Item.ReviewReason,
        Estimate = EstimateOf(keyed.Item),
        DueAt = keyed.Item.DueAt,
        Notes = keyed.Item.Notes,
    };

    private static VoiceReviewItemDocument ToReviewItem(KeyedVoiceItem keyed) => new()
    {
        Key = keyed.Key,
        Title = keyed.Item.Title,
        Domain = keyed.Item.Domain,
        Priority = keyed.Item.Priority,
        Confidence = keyed.Item.Confidence,
        ReviewReason = keyed.Item.ReviewReason,
        Reasons = keyed.Item.Reasons.ToList(),
        Estimate = EstimateOf(keyed.Item),
        DueRaw = keyed.Item.DueRaw,
        DueAt = keyed.Item.DueAt,
        Notes = keyed.Item.Notes,
    };

    /// <summary>
    /// Only called for items the gate routed to the clarify lane, so the
    /// clarification block is always set — Node throws outright if it is not, and a
    /// silent skip here would lose the question instead of surfacing the bug.
    /// </summary>
    private static VoiceClarifyItemDocument ToClarifyItem(KeyedVoiceItem keyed)
    {
        var clarification = keyed.Item.Clarification
            ?? throw new InvalidOperationException("draftToClarifyItem called on a non-clarify item");

        return new VoiceClarifyItemDocument
        {
            Key = keyed.Key,
            Title = keyed.Item.Title,
            Domain = keyed.Item.Domain,
            Priority = keyed.Item.Priority,
            Question = clarification.Question,
            Kind = clarification.Kind,
            CostOfWrong = clarification.CostOfWrong,
            Options = clarification.Options
                .Select(o => new VoiceClarifyOptionDocument { Label = o.Label, DueAt = o.DueAt })
                .ToList(),
            Notes = keyed.Item.Notes,
        };
    }

    /// <summary>
    /// An estimate needs BOTH bounds. An absent estimate is honest; a half-invented
    /// one is not.
    /// </summary>
    private static TaskEstimateDocument? EstimateOf(DraftVoiceItem item) =>
        item is { EstimateMinMinutes: { } min, EstimateMaxMinutes: { } max }
            ? new TaskEstimateDocument { MinMinutes = min, MaxMinutes = max, Source = "ai" }
            : null;
}
