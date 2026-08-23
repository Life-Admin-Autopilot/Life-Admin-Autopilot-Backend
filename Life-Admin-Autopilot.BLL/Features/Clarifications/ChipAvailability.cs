using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// A date question may not offer a time the user is already busy at.
///
/// <para>
/// <b>Why this exists.</b> The chips come from
/// <see cref="VoiceNotes.VoiceAutoFilePolicy"/>, which composes them from the clock
/// — tomorrow at nine, this time next week — and has never seen the user's other
/// matters. On an empty calendar that is fine. On a real one the server offered
/// "بكرة — 9:00 ص" for a morning that already held a licence renewal, the user
/// tapped it because the app had suggested it, and the app then told them off for
/// the clash it had just proposed. Suggesting a slot and then objecting to it is
/// worse than not suggesting one.
/// </para>
///
/// <para>
/// <b>Dropped, then topped back up.</b> Removing a taken chip alone would leave a
/// date question with one answer on a busy week, so the gap is refilled from
/// <see cref="SlotSuggester"/> — the same source, and checked against the same
/// pool, that the clash panel offers when a matter has already landed badly. A
/// suggestion taken here therefore cannot be refused a moment later.
/// </para>
///
/// <para>
/// <b>Never throws.</b> The task is written by the time this runs. A question with
/// an imperfect chip is a far better outcome than a 500 on a request whose real
/// work already succeeded, so any failure yields the original chips untouched.
/// </para>
/// </summary>
public static class ChipAvailability
{
    /// <summary>
    /// One chip. <paramref name="Title"/> and <paramref name="Notes"/> are the
    /// per-option overrides a hold may carry — this cares about neither, and they
    /// ride along so that a chip which survives the check survives it INTACT.
    /// </summary>
    public readonly record struct Chip(
        string Label,
        DateTime? DueAt,
        string? Title = null,
        string? Notes = null);

    public static async Task<IReadOnlyList<Chip>> FreeOnlyAsync(
        ConflictService conflicts,
        ObjectId userId,
        TaskDocument task,
        IReadOnlyList<Chip> chips,
        string? timezone,
        CancellationToken cancellationToken = default)
    {
        // "No date needed" is the only chip on some questions, and it can never
        // clash with anything.
        var dated = chips.Where(c => c.DueAt.HasValue).ToList();
        if (dated.Count == 0)
        {
            return chips;
        }

        try
        {
            var pool = await conflicts.OpenMattersAsync(userId, cancellationToken).ConfigureAwait(false);
            var candidate = ConflictService.MatterCandidate.From(task);

            // Excluding the matter itself. It is already saved — a hold writes the
            // task before it writes the question — so a check that did not exclude
            // it would find every chip clashing with the very matter being asked
            // about.
            bool Taken(DateTime at) =>
                ConflictService.ClashesWithin(at, candidate, pool, task.Id);

            var kept = chips.Where(c => !c.DueAt.HasValue || !Taken(c.DueAt.Value)).ToList();

            var lost = dated.Count - kept.Count(c => c.DueAt.HasValue);
            if (lost == 0)
            {
                return chips;
            }

            var desired = dated[0].DueAt!.Value;
            var free = SlotSuggester.Suggest(
                task.Title ?? string.Empty,
                desired,
                OffsetFor(timezone, desired),
                Taken,
                wanted: lost);

            foreach (var at in free)
            {
                kept.Add(new Chip(ChatGapText.ChipLabelFor(at, task.Title ?? string.Empty, timezone), at));
            }

            return kept;
        }
        catch (Exception)
        {
            return chips;
        }
    }

    private static TimeSpan OffsetFor(string? timezone, DateTime at)
    {
        if (string.IsNullOrEmpty(timezone))
        {
            return TimeSpan.Zero;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone)
                .GetUtcOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeSpan.Zero;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeSpan.Zero;
        }
    }
}
