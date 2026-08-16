using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// The real transcript → items seam: <see cref="IVoiceExtractor"/> over the SAME
/// extraction <c>POST /api/planning/propose</c> uses, plus the auto-file policy that
/// decides which items carry a question.
///
/// <para>
/// <b>Replaces <see cref="NullVoiceExtractor"/></b>, which throws before any model
/// call.
/// </para>
///
/// <para>
/// <b>Why this does not go through Langflow.</b> Two reasons, and the second is the
/// one that settles it. The planning agent's tools write to Mongo the moment it
/// calls them, which is the opposite of what an extraction pass wants — the same
/// argument <c>PlanningService</c> already makes for the propose route. And the flow
/// authenticates as the USER, with the user's bearer token, which a background
/// worker does not have and must not manufacture. So the worker calls the model
/// directly, with no tools bound and nothing it can write with.
/// </para>
///
/// <para>
/// <b>Why extraction is shared but the conflict pass is not.</b> Sharing extraction
/// is the point: the same sentence typed and spoken must produce the same matters,
/// or neither answer is explainable. The conflict pass is re-run here because this
/// path needs the result for a different purpose — the propose route shows conflicts
/// to a human who is about to decide, while this one uses them to decide whether a
/// human needs to be asked at all.
/// </para>
/// </summary>
public sealed class PlanningVoiceExtractor : IVoiceExtractor
{
    private readonly PlanningService _planning;
    private readonly ConflictService _conflicts;
    private readonly ILogger<PlanningVoiceExtractor> _logger;

    public PlanningVoiceExtractor(
        PlanningService planning,
        ConflictService conflicts,
        ILogger<PlanningVoiceExtractor> logger)
    {
        _planning = planning;
        _conflicts = conflicts;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DraftVoiceItem>> ExtractAsync(
        VoiceExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Reduced ONCE, here. Everything downstream — the option instants, the day
        // name on the question — assumes a zone this machine can resolve, and the
        // upload route does not guarantee one.
        var timezone = VoiceAutoFilePolicy.ResolveZone(request.Timezone);

        if (timezone is null && !string.IsNullOrWhiteSpace(request.Timezone))
        {
            // Worth a line: the note still processes, but every local time in it was
            // read as UTC, and that is invisible in the result.
            _logger.LogWarning("voiceNote:unusable-timezone value={Timezone}", request.Timezone);
        }

        var drafts = await _planning
            .ExtractDraftsAsync(request.Transcript, timezone, request.SpokenAt, cancellationToken)
            .ConfigureAwait(false);

        if (drafts.Count == 0)
        {
            return Array.Empty<DraftVoiceItem>();
        }

        // One read of the user's open matters for the whole note, reused for every
        // draft — the same shape the propose route uses, for the same reason.
        var open = await _conflicts.OpenMattersAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        var items = new List<DraftVoiceItem>(drafts.Count);
        foreach (var draft in drafts)
        {
            var found = await _conflicts
                .CheckAsync(request.UserId, draft.Title, draft.DueAt, open, excludeTaskId: null, cancellationToken)
                .ConfigureAwait(false);

            items.Add(VoiceAutoFilePolicy.Apply(
                draft with
                {
                    Conflicts = found
                        .Select(c => new PlanningConflict(c.TaskId, c.Title, c.DueAt, c.Reason))
                        .ToList(),
                },
                timezone));
        }

        _logger.LogInformation(
            "voiceNote:extracted items={ItemCount} held={HeldCount}",
            items.Count,
            items.Count(i => i.Clarification is not null));

        return items;
    }
}
