using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
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
    /// How many free slots the clash question offers.
    ///
    /// <para>
    /// Two, beside "keep this time" — three chips is what the card can show without
    /// scrolling, and a list of alternatives long enough to need scanning turns a
    /// one-tap answer back into a decision. <see cref="SlotSuggester"/> returns them
    /// soonest-first, so the two taken are the two most likely to suit.
    /// </para>
    /// </summary>
    private const int MaxSuggestedSlots = 2;

    /// <summary>
    /// Turn one planning draft into the voice slice's item shape, with a
    /// clarification attached when the draft is unsettled.
    /// </summary>
    /// <param name="timezone">
    /// Already reduced to a zone this machine recognises, or null. Callers must not
    /// pass the note's raw stored value — see <see cref="ResolveZone"/>.
    /// </param>
    public static DraftVoiceItem Apply(
        TaskDraft draft,
        string? timezone,
        IReadOnlyList<DateTime>? freeSlots = null)
    {
        var clash = draft.Conflicts.FirstOrDefault(c => !IsDuplicate(c));
        var duplicate = draft.Conflicts.FirstOrDefault(IsDuplicate);
        var unsure = draft.Confidence < ConfidenceFloor;

        // A clash outranks an assumed time, and that order is a correction.
        //
        // It used to run the other way, so a draft that both guessed a clock time AND
        // landed on top of something else was only ever asked "what time?" — the
        // collision was filed silently and never mentioned, because one item carries
        // one question. That is backwards on both counts. The clash is the more
        // expensive mistake (a double-booking rather than an hour out), and it is
        // also the one the user cannot discover for themselves, whereas a wrong hour
        // is visible the moment they look at the matter.
        //
        // Nothing is lost by the swap: the clash question is itself a date question
        // whose options are real times, so answering it settles the assumed time in
        // the same tap. Only the wording differs, and the clash's wording is the one
        // that explains why it is being asked.
        // A MISSING FIGURE OUTRANKS A GUESSED HOUR, by the same test the paragraph
        // above applies to the clash: which mistake can the user find on their own?
        // A wrong hour is written on the matter and visible the moment they look at
        // it. A missing amount is visible nowhere — the matter reads complete and
        // the money tab just quietly under-reports, because it totals `task.amount`
        // and this one has none. It does NOT outrank a missing DATE: an undated
        // matter never resurfaces at all, so `NeedsADate` still gets the question
        // and the figure goes unasked. One item carries one question, and that is
        // the tie-break.
        var clarification =
            clash is not null ? AskAboutTheClash(draft, clash, timezone, freeSlots)
            : NeedsAnAmount(draft) && draft.DueAt.HasValue ? AskForTheAmount(draft)
            : draft.TimeAssumed && draft.DueAt.HasValue ? AskForTheTime(draft, timezone)
            : duplicate is not null ? AskAboutTheDuplicate(draft, duplicate)
            : unsure ? AskWhetherItIsReal(draft)
            : NeedsADate(draft) ? AskWhenItIsDue(draft, timezone)
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
            ReviewReason: ReviewReasonFor(
                clarification, clash ?? duplicate, unsure, NeedsADate(draft), NeedsAnAmount(draft)),
            Reasons: draft.Conflicts.Select(c => c.Reason).ToList(),
            EstimateMinMinutes: null,
            EstimateMaxMinutes: null,
            DueRaw: null,
            DueAt: draft.DueAt,
            Notes: draft.Notes,
            Clarification: clarification);
    }

    /// <summary>
    /// The gaps in a matter that is ALREADY SAVED — the CHAT lane's entry into the
    /// very same questions the voice lane asks.
    ///
    /// <para>
    /// <b>Why chat needs this at all.</b> Chat's questions are model-authored: the
    /// prompt tells the agent to call <c>holdForClarification</c> when a matter has
    /// no date, and for most sentences it does. For some it does not — measured
    /// 2026-08-22, "اشتري لبن" was held every time and "تعلم سباحة" was filed
    /// silently every time, because the model reads the second as an aspiration
    /// rather than a to-do and reasons its way past the instruction. No wording
    /// fixes that reliably; a matter with no date is a FACT about the saved row, and
    /// facts belong on the server. This is what makes the question unconditional.
    /// </para>
    ///
    /// <para>
    /// <b>Deliberately the same functions <see cref="Apply"/> calls.</b> Two lanes
    /// asking a user the same thing in two different sentences is the bug this
    /// avoids by construction — not by two implementations kept in step. Nothing
    /// about <see cref="Apply"/> changes: voice still asks ONE question per item and
    /// still ranks a clash and a duplicate above these, neither of which is knowable
    /// here anyway (chat checks clashes on its own, at save, in the tool).
    /// </para>
    ///
    /// <para>
    /// <b>Chat may ask BOTH, where voice asks one.</b> Voice items are auto-filed in
    /// bulk from one utterance and each carries a single question; chat holds one
    /// matter at a time and the card stack answers them independently. So a renewal
    /// with neither a date nor a figure gets both gaps here, where voice would ask
    /// the date and let the money go — the tie-break in <see cref="Apply"/> that
    /// exists because one item carries one question.
    /// </para>
    /// </summary>
    /// <param name="timeAssumed">
    /// True when the HOUR on <paramref name="draft"/> was picked by the agent rather
    /// than said by the user. There is no way to infer this from the saved row — a
    /// 09:00 the user asked for and a 09:00 the model reached for are the same
    /// instant — so it is reported by the caller, exactly as
    /// <see cref="TaskDraft.TimeAssumed"/> is reported by the voice extractor.
    /// </param>
    public static IReadOnlyList<DraftClarification> GapsFor(
        TaskDraft draft,
        string? timezone,
        bool timeAssumed)
    {
        var gaps = new List<DraftClarification>(2);

        // No date, or a date whose hour we invented — never both, because the first
        // question already asks for the day AND the time.
        if (NeedsADate(draft))
        {
            gaps.Add(AskWhenItIsDue(draft, timezone));
        }
        else if (timeAssumed && draft.DueAt.HasValue)
        {
            gaps.Add(AskForTheTime(draft, timezone));
        }

        if (NeedsAnAmount(draft))
        {
            gaps.Add(AskForTheAmount(draft));
        }

        return gaps;
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
            new(Clock(local), guess, "chip.at", At(guess)),
        };

        foreach (var (hour, label) in TimeChoices)
        {
            if (hour == local.Hour)
            {
                continue;
            }

            var at = AtLocalHour(local, hour, timezone);
            options.Add(new DraftClarifyOption(
                $"{label} — {Clock(at.Local)}",
                at.Utc,
                $"chip.{label.ToLowerInvariant()}",
                At(at.Utc)));
        }

        return new DraftClarification(
            $"What time on {Day(local)}?",
            "date",
            CostOfWrong(draft),
            options,
            "ask.whatTimeOn",
            At(guess));
    }

    /// <summary>
    /// It lands inside the two-hour window around something they already have. The
    /// question is a date question, because the only thing that resolves it is a
    /// different time.
    /// </summary>
    private static DraftClarification AskAboutTheClash(
        TaskDraft draft,
        PlanningConflict clash,
        string? timezone,
        IReadOnlyList<DateTime>? freeSlots)
    {
        var guess = draft.DueAt;

        var options = new List<DraftClarifyOption>
        {
            new("Keep this time", guess, "chip.keepThisTime"),
        };

        // Real free slots when the caller found any, and nothing when it did not.
        //
        // What used to be here was `guess + ClashShift` — a time no one had checked,
        // offered as though it were an answer. Roughly half the time it lands on the
        // very matter being clashed with (a two-hour window shifted by two and a
        // half), so accepting the suggestion recreated the clash, and the question
        // could not be asked a second time because it had already been answered.
        //
        // An offer of nothing is the honest alternative when the week is genuinely
        // full: the card still carries Keep, Skip and Discard, and the user can type
        // a time. A wrong suggestion is worse than no suggestion here, because this
        // one gets tapped without being read.
        foreach (var slot in (freeSlots ?? Array.Empty<DateTime>()).Take(MaxSuggestedSlots))
        {
            options.Add(new DraftClarifyOption(
                SlotLabel(slot, guess, timezone),
                slot,
                SameLocalDay(slot, guess, timezone) ? "chip.laterThatDay" : "chip.dayAt",
                At(slot)));
        }

        return new DraftClarification(
            $"This is close to \"{clash.Title}\". Keep both?",
            "date",
            ClarificationVocabulary.CostHigh,
            options,
            "ask.closeTo",
            new Dictionary<string, string> { ["title"] = clash.Title });
    }

    /// <summary>
    /// A free slot, named by when it actually is. Same day says the clock time alone;
    /// another day names the day, because "14:00" on a card the user opens tomorrow
    /// is otherwise a time with no date attached.
    /// </summary>
    private static string SlotLabel(DateTime slot, DateTime? from, string? timezone)
    {
        var local = ToLocal(slot, timezone);

        if (SameLocalDay(slot, from, timezone))
        {
            return $"Later that day — {Clock(local)}";
        }

        return $"{Day(local)} — {Clock(local)}";
    }

    /// <summary>
    /// Whether two instants land on the same day for the user. Extracted so the
    /// English label and its i18n key cannot drift onto different branches.
    /// </summary>
    private static bool SameLocalDay(DateTime slot, DateTime? from, string? timezone) =>
        from is { } origin && ToLocal(origin, timezone).Date == ToLocal(slot, timezone).Date;

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
            new[] { new DraftClarifyOption("Keep both", draft.DueAt, "chip.keepBoth") },
            "ask.alreadyHave",
            new Dictionary<string, string> { ["title"] = duplicate.Title });

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
            new[] { new DraftClarifyOption("Yes, that is right", draft.DueAt, "chip.yesThatIsRight") },
            "ask.didYouMean",
            new Dictionary<string, string> { ["title"] = draft.Title });

    /// <summary>
    /// Filed with no date, so nothing will ever bring it back.
    ///
    /// <para>
    /// <b>This asks about EVERY undated item, and that is a deliberate reversal.</b>
    /// The note further up argues that "buy milk" has no date because there is no
    /// date, and that asking manufactures uncertainty. The counter-argument, and the
    /// one the product owner took: an undated matter is filed into a list the user
    /// has to remember to open, which is the habit the app exists to replace. Asking
    /// once, cheaply, is better than a matter that quietly never surfaces.
    /// </para>
    ///
    /// <para>
    /// <b>It was tried the narrow way first and the signal does not exist.</b> The
    /// gate keyed on <c>Kind == "reminder" &amp;&amp; DueAt is null</c>, on the reading that
    /// <c>kind</c> carried the model's judgement about whether a matter must happen by
    /// a deadline. It does not: <c>PlanningService</c>'s prompt says <i>"use 'reminder'
    /// only when a time is known, otherwise 'list' with dueAt null"</i>, which makes
    /// <c>kind</c> a restatement of <c>dueAt</c> and that pair unreachable. Measured on
    /// real notes — "renew my car insurance" came back <c>list</c>, and no question was
    /// ever raised. Do not reinstate that gate without changing the prompt first, and
    /// note the prompt is shared with chat.
    /// </para>
    ///
    /// <para>
    /// The cost is carried by the answer rather than the question: option zero is
    /// "No date needed", one tap, and it leaves the matter exactly as it was.
    /// </para>
    /// </summary>
    private static bool NeedsADate(TaskDraft draft) => draft.DueAt is null;

    /// <summary>
    /// They named no day. Offer two ordinary ones, and the answer that leaves it
    /// alone.
    ///
    /// <para>
    /// <b>Option ZERO carries a null date, and that is load-bearing</b> for the same
    /// reason it is in <see cref="AskForTheTime"/>: <see cref="VoiceClarificationStaging"/>
    /// files the task at <c>Options[0].DueAt</c>. The reading this item was filed
    /// under is UNDATED, so anything else in that slot would quietly give a date to a
    /// matter the user never dated — while the card claimed to be offering them the
    /// choice.
    /// </para>
    ///
    /// <para>
    /// Anchored on now rather than on the draft, because there is no <c>DueAt</c> to
    /// anchor on — that absence is the whole question.
    /// </para>
    /// </summary>
    private static DraftClarification AskWhenItIsDue(TaskDraft draft, string? timezone)
    {
        var localNow = ToLocal(DateTime.UtcNow, timezone);
        var hour = TimeChoices[0].Hour;

        var tomorrow = AtLocalHour(localNow.AddDays(1), hour, timezone);
        var nextWeek = AtLocalHour(localNow.AddDays(7), hour, timezone);

        return new DraftClarification(
            $"When is \"{draft.Title}\" due?",
            "date",
            CostOfWrong(draft),
            new[]
            {
                new DraftClarifyOption("No date needed", null, "chip.noDateNeeded"),
                new DraftClarifyOption(
                    $"Tomorrow — {Clock(tomorrow.Local)}", tomorrow.Utc, "chip.tomorrowAt", At(tomorrow.Utc)),
                new DraftClarifyOption(
                    $"{Day(nextWeek.Local)} — {Clock(nextWeek.Local)}", nextWeek.Utc, "chip.dayAt", At(nextWeek.Utc)),
            },
            "ask.whenDue",
            new Dictionary<string, string> { ["title"] = draft.Title });
    }

    /// <summary>
    /// A matter about money moving, with no figure on it.
    ///
    /// <para>
    /// <b>Domain is the first signal and it is not enough on its own.</b> The
    /// extractor classifies, and where it says <c>finance</c> that judgement is
    /// already made and worth trusting. But it is not stable: measured 2026-08-22,
    /// the one sentence "لازم أدفع فاتورة الكهرباء يوم الخميس" came back
    /// <c>finance</c> through chat and <c>home</c> through the voice extractor,
    /// which run different prompts. Keying only on the domain meant the commonest
    /// money matter there is — a utility bill — was asked nothing.
    /// </para>
    ///
    /// <para>
    /// <b>So the title is read too, in both languages.</b>
    /// <see cref="ReminderLeadTime.MatchKeyword"/> is the existing precedent for a
    /// regex table over matter titles and it already names Bill, Subscription, Tax
    /// and Insurance — but every pattern in it is English, so on an Arabic title it
    /// matches nothing and falls through to the domain default. That is worth fixing
    /// there; it is not worth coupling this to it in the meantime, because the
    /// question being asked here is narrower ("is money moving?") than the question
    /// asked there ("how far ahead should this nudge?").
    /// </para>
    /// </summary>
    private static bool NeedsAnAmount(TaskDraft draft) =>
        draft.Amount is null
        && (string.Equals(draft.Domain, "finance", StringComparison.Ordinal)
            || MoneyWords.IsMatch(draft.Title));

    /// <summary>
    /// Money moving, named in the title. Deliberately short: a false positive costs
    /// one answerable question the user can skip, a false negative costs a figure
    /// that never reaches the money tab, so the list leans towards asking.
    ///
    /// <para>
    /// <b>Renewals are in the list, and they are the reason it is not domain-only.</b>
    /// "هجدد رخصة العربية" classifies as <c>car</c>, not <c>finance</c> — correctly,
    /// it IS about the car — and a licence renewal costs money every single time.
    /// The same holds for an insurance renewal, a subscription, a permit. Keying on
    /// the domain asked nothing for the whole category.
    /// </para>
    /// </summary>
    private static readonly Regex MoneyWords = new(
        @"\bbill\b|\brent\b|invoice|payment|\bpay\b|instal?ment|mortgage|utilit|subscription|\bfees?\b|\btax(es)?\b|\bfine\b"
        + @"|\brenew(al|als|ing)?\b"
        + @"|فاتور|إيجار|ايجار|قسط|أقساط|اقساط|رسوم|اشتراك|ضريب|غرامة|أدفع|ادفع|يدفع|سدد|تسديد"
        + @"|تجديد|أجدد|اجدد|هجدد|يجدد|نجدد|جدّد|جدد",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The figure, typed. No options: an amount is not a short list, and a chip
    /// offering one would be the app inventing what a bill costs — which
    /// <c>NEVER INVENT ONE</c> in the chat prompt forbids for the same reason.
    ///
    /// <para>
    /// The task keeps the date it was filed under. Every other question here is a
    /// date question whose option zero carries that date, so <c>staging</c> reads
    /// the guess off the options; this one has none, and
    /// <see cref="VoiceClarifyItemDocument.DueAt"/> is what stops the matter losing
    /// a date the user did give.
    /// </para>
    /// </summary>
    private static DraftClarification AskForTheAmount(TaskDraft draft) =>
        new(
            $"How much is \"{draft.Title}\"?",
            "detail",
            ClarificationVocabulary.CostHigh,
            Array.Empty<DraftClarifyOption>(),
            "ask.howMuch",
            new Dictionary<string, string> { ["title"] = draft.Title });

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
        bool unsure,
        bool needsADate,
        bool needsAnAmount) =>
        clarification is null ? "clear"
        // The two conflict kinds are now told apart. Every clash used to report
        // itself as a possible duplicate, which is a different claim about the
        // user's list and the wrong one for a matter they have never seen before.
        : conflict is not null
            ? IsDuplicate(conflict) ? "maybe_duplicate" : "time_clash"
        : unsure ? "ambiguous_intent"
        // INCOMPLETE, not vague. Nothing about a date was unclear here, because no
        // date was given at all — "vague_date" would claim the user said something
        // ambiguous when they said nothing.
        : needsADate ? "incomplete"
        // Same claim as a missing date, and for the same reason: a figure was never
        // given, so nothing the user said was unclear.
        : needsAnAmount ? "incomplete"
        : "vague_date";

    /// <summary>
    /// Read off the conflict's own <c>Kind</c>, not out of its prose.
    ///
    /// <para>
    /// This used to be <c>Reason.Contains("already")</c>, which asked a sentence
    /// written for a human to act as a type tag. It worked only for as long as
    /// nobody reworded the reason and only in English — a localised reason would
    /// have classified every duplicate as a time clash, and the user would have been
    /// asked to move a matter that did not need moving instead of being told they
    /// already had it. <see cref="PlanningConflict.Kind"/> now carries the answer the
    /// detector already knew.
    /// </para>
    /// </summary>
    private static bool IsDuplicate(PlanningConflict conflict) =>
        string.Equals(conflict.Kind, MatterConflict.Duplicate, StringComparison.Ordinal);

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

    /// <summary>
    /// The one i18n parameter every dated string here needs: the instant itself,
    /// as round-trip UTC.
    ///
    /// <para>
    /// <b>Not the formatted day or clock time.</b> Those are what
    /// <see cref="Day"/> and <see cref="Clock"/> produce for the English fallback,
    /// and they are baked in the invariant culture with a 24-hour clock. Handing
    /// the client the instant instead lets it use the formatters it already has
    /// for every other date on screen, so a chip reads ٩:٠٠ ص beside an Arabic
    /// question and 9:00 AM beside an English one — matching the rest of the card
    /// rather than the server's locale.
    /// </para>
    /// </summary>
    private static Dictionary<string, string> At(DateTime utc) =>
        new() { ["at"] = utc.ToUniversalTime().ToString("o") };
}
