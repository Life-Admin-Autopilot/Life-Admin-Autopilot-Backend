using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// The rule that decides, for one extracted draft, whether it is filed silently or
/// filed WITH a question attached. This is the whole of "voice is fire-and-forget":
/// the user speaks, the surface closes, and every matter lands — the only variable
/// is whether something is asked about it afterwards, in the notification feed, at
/// a moment of their choosing.
///
/// <para>
/// <b>The AI never asks during the interaction.</b> That is the product rule this
/// implements, and it is why uncertainty becomes a persisted Clarification rather
/// than a prompt: a question the user has to answer before their thought is captured
/// defeats the point of capturing it by voice.
/// </para>
///
/// <para>
/// <b>Three things make a draft unsettled</b>, and each is a real gap rather than a
/// confidence score dressed up as one:
/// </para>
/// <list type="number">
///   <item>
///     <b>The clock time is ours, not theirs</b> (<c>TimeAssumed</c>). "Remind me
///     Monday to go to the dentist" gives a day and no time; a task row stores one
///     instant, so the extractor picks 09:00 — and left unmarked that guess is
///     presented to the user as though they had chosen it. This is the case the
///     whole mechanism exists for.
///   </item>
///   <item>
///     <b>It clashes with something they already have.</b> Filing a second matter
///     inside the two-hour window silently is how a double-booking gets made without
///     anyone deciding to make one.
///   </item>
///   <item>
///     <b>The model is not sure the item was requested at all</b> (confidence below
///     <see cref="ConfidenceFloor"/>). No date option can answer that, so it is
///     asked as a plain confirmation.
///   </item>
/// </list>
///
/// <para>
/// <b>Where this departs from the literal specification, and why.</b> The written
/// rule says a settled draft "has a user-given date". Read strictly, an item with no
/// date at all — "buy milk", "call the landlord" — would be unsettled and would be
/// asked "when?". That manufactures uncertainty rather than surfacing it: the user
/// did not give a date because there is no date, the extractor guessed nothing, and
/// a list matter with no due date is the correct and complete answer. So an undated,
/// confident, unclashing draft is SETTLED here. Nothing was assumed, so there is
/// nothing to confirm.
/// </para>
/// </summary>
public static class VoiceAutoFilePolicy
{
    /// <summary>
    /// Below this the model is telling us it is not sure the user asked for the item.
    /// Distinct from the extractor's own quality: a confident reading of a mumbled
    /// sentence still scores low, which is exactly when a confirmation is worth
    /// asking.
    /// </summary>
    public const double ConfidenceFloor = 0.6;

    /// <summary>
    /// The domains where being a day out costs something you cannot take back — a
    /// missed payment, a missed appointment, a missed dose. Everything else is a
    /// reschedule.
    /// </summary>
    private static readonly string[] ExpensiveDomains = { "finance", "health" };

    /// <summary>The priorities that say the same thing.</summary>
    private static readonly string[] ExpensivePriorities = { "high", "urgent" };

    /// <summary>The local hours offered when the user named a day but not a time.</summary>
    private static readonly (int Hour, string Label)[] TimeChoices =
    {
        (9, "Morning"),
        (14, "Afternoon"),
        (18, "Evening"),
    };

    /// <summary>
    /// How far a clashing matter is offered to move.
    ///
    /// <para>
    /// A coarse affordance, not the detection rule. It used to be derived from the
    /// old fixed two-hour clash radius, which no longer exists now that a clash is
    /// window overlap; the offered jump keeps its previous size deliberately, so this
    /// card does not change behaviour for a reason unrelated to it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ClashShift = ConflictService.SuggestedShift;

    /// <summary>
    /// Turn one planning draft into the voice slice's item shape, with a
    /// clarification attached when the draft is unsettled.
    /// </summary>
    /// <param name="timezone">
    /// Already reduced to a zone this machine recognises, or null. Callers must not
    /// pass the note's raw stored value — see <see cref="ResolveZone"/>.
    /// </param>
    public static DraftVoiceItem Apply(TaskDraft draft, string? timezone)
    {
        var clash = draft.Conflicts.FirstOrDefault(c => !IsDuplicate(c));
        var duplicate = draft.Conflicts.FirstOrDefault(IsDuplicate);
        var unsure = draft.Confidence < ConfidenceFloor;

        var clarification =
            draft.TimeAssumed && draft.DueAt.HasValue ? AskForTheTime(draft, timezone)
            : clash is not null ? AskAboutTheClash(draft, clash)
            : duplicate is not null ? AskAboutTheDuplicate(draft, duplicate)
            : unsure ? AskWhetherItIsReal(draft)
            : null;

        return new DraftVoiceItem(
            draft.Title,
            draft.Domain,
            draft.Priority,

            // The gate reads these two, and a clarified item is routed by the
            // presence of the clarification rather than by either of them — so they
            // describe the item honestly instead of steering it. An item with no
            // question is by definition one nothing was flagged on.
            Confidence: clarification is null ? "high" : "low",
            ReviewReason: ReviewReasonFor(clarification, clash ?? duplicate, unsure),
            Reasons: draft.Conflicts.Select(c => c.Reason).ToList(),
            EstimateMinMinutes: null,
            EstimateMaxMinutes: null,
            DueRaw: null,
            DueAt: draft.DueAt,
            Notes: draft.Notes,
            Clarification: clarification);
    }

    /// <summary>
    /// The note's timezone, or null when this machine cannot make sense of it.
    ///
    /// <para>
    /// <c>POST /me/voice-notes</c> stores whatever 1..64 character string the client
    /// sent, with no IANA check — that asymmetry with the <c>extract-tasks</c> body
    /// is deliberate and documented, because adding a check would reject uploads the
    /// reference server accepts. The consequence lands here: a note can carry
    /// <c>"Mars/Olympus"</c>, and every downstream zone lookup would throw. Reducing
    /// it to null once, at the boundary, is what stops one bad header failing a note
    /// whose audio was perfectly good.
    /// </para>
    /// </summary>
    public static string? ResolveZone(string? timezone) =>
        ImportedTimeResolver.IsValidTimeZone(timezone) ? timezone : null;

    // ---- the three questions ------------------------------------------------

    /// <summary>
    /// They named the day; the clock time is the extractor's. Offer the guess plus
    /// the other two ordinary parts of that same day.
    ///
    /// <para>
    /// <b>The guess is option ZERO and that is load-bearing.</b>
    /// <see cref="VoiceClarificationStaging"/> files the task at
    /// <c>Options[0].DueAt</c>, so the first option has to be the reading the item
    /// was actually filed under — otherwise the card offers a "keep it as it is"
    /// answer that silently moves it.
    /// </para>
    /// </summary>
    private static DraftClarification AskForTheTime(TaskDraft draft, string? timezone)
    {
        var guess = draft.DueAt!.Value;
        var local = ToLocal(guess, timezone);

        var options = new List<DraftClarifyOption>
        {
            new(Clock(local), guess),
        };

        foreach (var (hour, label) in TimeChoices)
        {
            if (hour == local.Hour)
            {
                continue;
            }

            var at = AtLocalHour(local, hour, timezone);
            options.Add(new DraftClarifyOption($"{label} — {Clock(at.Local)}", at.Utc));
        }

        return new DraftClarification(
            $"What time on {Day(local)}?",
            "date",
            CostOfWrong(draft),
            options);
    }

    /// <summary>
    /// It lands inside the two-hour window around something they already have. The
    /// question is a date question, because the only thing that resolves it is a
    /// different time.
    /// </summary>
    private static DraftClarification AskAboutTheClash(TaskDraft draft, PlanningConflict clash)
    {
        var guess = draft.DueAt;

        var options = new List<DraftClarifyOption> { new("Keep this time", guess) };

        if (guess is { } at)
        {
            var moved = at + ClashShift;
            options.Add(new DraftClarifyOption($"Later that day — {Clock(ToLocal(moved, null))}", moved));
        }

        return new DraftClarification(
            $"This is close to \"{clash.Title}\". Keep both?",
            "date",
            ClarificationVocabulary.CostHigh,
            options);
    }

    /// <summary>
    /// It reads as something they already have. Nothing about the DATE is in doubt,
    /// so this is a plain confirmation — and the card's own discard is the other
    /// answer.
    /// </summary>
    private static DraftClarification AskAboutTheDuplicate(TaskDraft draft, PlanningConflict duplicate) =>
        new(
            $"You already have \"{duplicate.Title}\". File this one as well?",
            "confirm",
            ClarificationVocabulary.CostHigh,
            new[] { new DraftClarifyOption("Keep both", draft.DueAt) });

    /// <summary>
    /// The model is not sure the sentence asked for anything. One affirming option;
    /// the card stack's Skip and Discard carry the other two answers, so a single
    /// chip is a complete choice rather than a dead end.
    /// </summary>
    private static DraftClarification AskWhetherItIsReal(TaskDraft draft) =>
        new(
            $"Did you mean to file \"{draft.Title}\"?",
            "confirm",
            CostOfWrong(draft),
            new[] { new DraftClarifyOption("Yes, that is right", draft.DueAt) });

    // ---- supporting judgements ----------------------------------------------

    /// <summary>
    /// Whether being wrong here is expensive.
    ///
    /// <para>
    /// <b>Read this before relying on it.</b> In the CHAT lane
    /// <c>ClarificationHoldService</c> uses <c>costOfWrong</c> to decide whether the
    /// provisional task may fire a reminder on the guess. In the VOICE lane it
    /// cannot: <c>VoiceNoteTaskPersistence</c> seeds every task it creates as
    /// <c>kind: "list"</c>, so a guessed date can never fire regardless of what this
    /// returns. The value still travels, because it is what the card renders and what
    /// the resolve path reads — but the safety property the specification attributes
    /// to it is, in this lane, held by the persistence layer instead. Anyone
    /// loosening that seed must move the guarantee here first.
    /// </para>
    /// </summary>
    private static string CostOfWrong(TaskDraft draft) =>
        ExpensiveDomains.Contains(draft.Domain, StringComparer.Ordinal)
        || ExpensivePriorities.Contains(draft.Priority, StringComparer.Ordinal)
            ? ClarificationVocabulary.CostHigh
            : ClarificationVocabulary.CostLow;

    /// <summary>
    /// The plain-language "why am I seeing this" line. Drawn from
    /// <c>VoiceNoteVocabulary.ReviewReasons</c> — a value outside that list renders
    /// as an unknown reason chip.
    /// </summary>
    private static string ReviewReasonFor(
        DraftClarification? clarification,
        PlanningConflict? conflict,
        bool unsure) =>
        clarification is null ? "clear"
        : conflict is not null ? "maybe_duplicate"
        : unsure ? "ambiguous_intent"
        : "vague_date";

    private static bool IsDuplicate(PlanningConflict conflict) =>
        conflict.Reason.Contains("already", StringComparison.OrdinalIgnoreCase)
        || conflict.Reason.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    // ---- time, always through the one normalizer -----------------------------

    /// <summary>
    /// The same wall clock, an hour moved.
    ///
    /// <para>
    /// Goes out through <see cref="HoldTimeNormalizer"/> rather than doing the offset
    /// arithmetic here, and that is not ceremony: it is the one implementation of
    /// "what instant does this local time mean", it already handles the zone the
    /// machine does not recognise, and <c>POST /me/clarifications</c> resolves its
    /// options through exactly the same call. Two implementations would put the same
    /// question on two cards at two different instants across a DST boundary.
    /// </para>
    /// </summary>
    private static (DateTime Utc, DateTime Local) AtLocalHour(DateTime local, int hour, string? timezone)
    {
        var naive = $"{local:yyyy-MM-dd}T{hour:00}:00";
        var utc = HoldTimeNormalizer.Normalize(naive, timezone);
        return (utc, ToLocal(utc, timezone));
    }

    private static DateTime ToLocal(DateTime utc, string? timezone) =>
        timezone is null
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(timezone));

    /// <summary>24-hour, because the app ships in English and Arabic and this reads the same in both.</summary>
    private static string Clock(DateTime local) => local.ToString("HH:mm");

    private static string Day(DateTime local) => local.ToString("dddd d MMMM");
}
