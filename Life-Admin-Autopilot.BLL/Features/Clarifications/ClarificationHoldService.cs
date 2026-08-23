using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>One pre-resolved suggested answer, dates already normalised.</summary>
public readonly record struct HoldOption(
    string Label,
    DateTime? DueAt,
    string? Title,
    string? Notes,
    /// <summary>
    /// Set only on a chip this SERVER composed. A model-written chip is already in
    /// the user's language and carries none.
    /// </summary>
    string? LabelKey = null,
    IReadOnlyDictionary<string, string>? LabelParams = null);

/// <summary>
/// A validated <c>holdForClarificationArgs</c>. Dates arrive as the RAW strings the
/// caller sent — normalisation needs the timezone and happens inside the service, so
/// the guess and every option go through one code path rather than two.
/// </summary>
/// <param name="Questions">
/// The multi-question form. EMPTY is the legacy single-question payload, and the one
/// row is built from <paramref name="Question"/>/<paramref name="Kind"/>/
/// <paramref name="Options"/> exactly as before. Non-empty, it is the complete list.
/// </param>
public sealed record HoldInput(
    string Title,
    string Domain,
    string Question,
    string Kind,
    string? Priority,
    IReadOnlyList<string>? Tags,
    string? Notes,
    string? DueAtGuess,
    string? CostOfWrong,
    IReadOnlyList<HoldRawOption> Options,
    string? SourceText,
    string? Timezone,
    IReadOnlyList<HoldRawQuestion>? Questions = null,
    /// <summary>Already through the money gate — minor units and a checked code, or null.</summary>
    MoneyDocument? Amount = null);

/// <summary>An option as it came off the wire — <c>dueAt</c> not yet an instant.</summary>
public readonly record struct HoldRawOption(string Label, string? DueAt, string? Title, string? Notes);

/// <summary>
/// One independently-answerable gap in a held matter.
///
/// <para>
/// A <c>null</c> member means the caller did not supply one and the top-level value
/// stands in. <see cref="Options"/> distinguishes "not supplied" (null → inherit the
/// top-level chips) from "supplied empty" (an empty list → this question has no
/// chips, the user types the answer), which is exactly the difference between a date
/// question and the <c>detail</c> gap riding alongside it.
/// </para>
/// </summary>
public readonly record struct HoldRawQuestion(
    string Question,
    string? Kind,
    string? CostOfWrong,
    IReadOnlyList<HoldRawOption>? Options);

/// <summary>
/// What the hold produced — ONE task, and one row per question asked about it.
/// </summary>
/// <param name="Clarification">
/// The FIRST row, or null when the queue was full and no question was filed at all.
/// Kept beside <paramref name="Clarifications"/> so a caller written against the
/// single-question response keeps working unchanged.
/// </param>
/// <param name="Clarifications">
/// Every row created, in the order the questions were asked. Empty exactly when
/// <paramref name="Clarification"/> is null.
/// </param>
public sealed record HoldOutcome(
    TaskDocument Task,
    ClarificationDocument? Clarification,
    bool QueueFull,
    IReadOnlyList<ClarificationDocument>? Clarifications = null)
{
    public IReadOnlyList<ClarificationDocument> Rows =>
        Clarifications ?? (Clarification is null
            ? Array.Empty<ClarificationDocument>()
            : new[] { Clarification });
}

/// <summary>
/// Port of <c>runHoldForClarification</c> in
/// <c>server/src/modules/ai/toolRunner.ts</c> — the whole of it, task included.
///
/// <para>
/// <b>Why the route creates the task too.</b> In Node the planning agent runs
/// IN-PROCESS, so this function is a private call and needs no endpoint. Our agent
/// runs in Langflow, outside the API, and the only thing it can do is make HTTP
/// requests — so the semantics have to live behind a route or they do not exist at
/// all. Splitting them (agent creates the task, then posts the question) would move
/// the one rule that matters — a guessed date must not be able to fire — into
/// model-adjacent Python that nothing enforces. It is here instead.
/// </para>
///
/// <para>
/// <b>The task is created ALWAYS, question or no question.</b> Withholding it left a
/// captured item invisible — not in Matters, not searchable, not deletable — until
/// the user answered. What is withheld now is the REMINDER, and only when a wrong
/// date is expensive: a <c>high</c>-cost item lands as <c>kind:'list'</c> and cannot
/// fire, while a <c>low</c>-cost one may nudge on the guess because being wrong there
/// just means rescheduling.
/// </para>
///
/// <para>
/// <b>ONE task, N questions.</b> A matter can have several gaps that are answered
/// independently, and until <c>questions</c> existed they had to be folded into one
/// sentence with one answer slot. On 2026-08-16 "remind me today to go to the friend"
/// was held once as "What time should I remind you — and which friend are you
/// visiting?" with time chips; the user tapped "9 am", the row resolved, the task
/// became a 9am reminder, and the which-friend gap ceased to exist. N gaps get N
/// rows now — all carrying the same <c>taskId</c>, each closing on its own.
/// </para>
/// </summary>
public sealed class ClarificationHoldService
{
    /// <summary>
    /// <c>MAX_OPEN_CLARIFICATIONS</c>. Past the cap the item is filed with the guess
    /// and the question is dropped: a slightly-wrong but VISIBLE task beats a
    /// question the user never reaches.
    /// </summary>
    public const int MaxOpenClarifications = 12;

    private readonly TaskWriteService _tasks;
    private readonly ClarificationRepository _clarifications;
    private readonly ReminderPlanner _reminders;

    public ClarificationHoldService(
        TaskWriteService tasks,
        ClarificationRepository clarifications,
        ReminderPlanner reminders)
    {
        _tasks = tasks;
        _clarifications = clarifications;
        _reminders = reminders;
    }

    public async Task<HoldOutcome> HoldAsync(
        ObjectId userId,
        HoldInput input,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        var options = Normalize(input.Options, input.Timezone);

        // The best guess we have, in priority order: an explicit one, else the
        // model's most-likely option — it orders them.
        var rawGuess = input.DueAtGuess;
        var dueAt = rawGuess is not null
            ? HoldTimeNormalizer.Normalize(rawGuess, input.Timezone)
            : options.FirstOrDefault().DueAt;

        // Defaults to 'high': if the model felt the item was worth asking about, do
        // not act on the guess.
        var costOfWrong = input.CostOfWrong ?? ClarificationVocabulary.CostHigh;
        var priority = input.Priority ?? "normal";
        var tags = input.Tags ?? Array.Empty<string>();

        // One question per gap, each answerable on its own. An empty `questions` is
        // the legacy payload and yields exactly the one row the top-level fields
        // describe, so the two forms are the same code path from here down.
        var questions = BuildQuestions(input, options, costOfWrong);

        // Whatever the model asked, the matter's real gaps get asked about too.
        //
        // This is the line that stops a chat-born matter being lost. The binder used
        // to REJECT a date question that carried nothing to tap, on the reasoning
        // that the tool hands the 400 back and the agent re-calls with options inside
        // the same turn. It does — most of the time. When it does not, `Parse` threw
        // before this method ran, no task was created, and the user was told "لم
        // أتمكن من إضافة المهمة" about a matter the server had simply refused to
        // write. Measured 2026-08-23: six dateless sentences in a row, six losses.
        //
        // A missing date is a FACT about the matter, not a modelling decision, so it
        // is answered here — with the SAME generator the voice lane and POST /me/tasks
        // already use, rather than a third implementation kept in step by hand.
        questions = WithServerGaps(questions, input, dueAt, priority);

        // The reminder is withheld if ANY question is expensive to get wrong. The
        // guard exists to stop a GUESSED date firing, and a matter is only as safe
        // as its riskiest open gap — a 'low' sibling cannot license the guess.
        //
        // Deliberately over EVERY question asked for, including any the queue cap
        // drops below. A gap the user will never be shown is unresolved forever, so
        // it is the last thing that should release a guess; counting only the filed
        // rows would let a full queue turn a high-cost hold into a firing reminder.
        // Withholding is never a dead end — the DATE question survives truncation
        // (see Prioritize) and answering it promotes the task exactly as usual.
        var effectiveCost = questions.Any(q => q.CostOfWrong == ClarificationVocabulary.CostHigh)
            ? ClarificationVocabulary.CostHigh
            : ClarificationVocabulary.CostLow;

        var kind = dueAt.HasValue && effectiveCost == ClarificationVocabulary.CostLow ? "reminder" : "list";

        var task = await _tasks
            .CreateAsync(
                userId,
                new TaskCreateInput(
                    input.Title,
                    input.Domain,
                    kind,
                    priority,
                    tags,
                    dueAt,
                    input.Notes,
                    Estimate: null,
                    // The figure the model was TOLD, never one it invented.
                    //
                    // This used to be hardcoded null, reasoning that an uncertain
                    // matter must not enter a spending total. That conflated two
                    // different fields: "send 500 EGP to Louisa" is held because
                    // the TIME is missing, and the money was the plainest fact in
                    // the sentence. Dropping it filed a finance matter that /money
                    // could not see, and the user had to retype a number they had
                    // already said. The gate upstream is what keeps an invented
                    // figure out — it takes only a stated amount with a resolvable
                    // currency, and yields null for everything else.
                    Amount: input.Amount,
                    SourceVoiceNoteId: null),
                now,
                cancellationToken)
            .ConfigureAwait(false);

        // `runCreate` schedules the rules floor for a dated reminder. Reachable here
        // only on the 'low'-cost branch — which is exactly the branch that is allowed
        // to fire on a guess. Note POST /me/tasks does NOT do this, in either server,
        // so an agent that created the task itself would leave the schedule empty.
        if (task.Kind == "reminder" && task.DueAt.HasValue)
        {
            await _reminders.SetRulesRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
        }

        // Queue full → the task above is the whole answer. Skip the questions rather
        // than growing a pile the user never reaches. The cap counts ROWS, so a
        // three-question hold spends three slots and a user one slot from the cap
        // gets asked one thing.
        var capacity = await CapacityAsync(userId, cancellationToken).ConfigureAwait(false);
        if (capacity <= 0)
        {
            return new HoldOutcome(task, null, QueueFull: true, Array.Empty<ClarificationDocument>());
        }

        var asked = Prioritize(questions, capacity);

        // A FRESH draft per row rather than one shared instance: the documents are
        // mutable and independent from here on, and an alias between two of them is
        // the kind of thing that only shows up as a bug much later.
        ClarificationDraftDocument NewDraft() => new()
        {
            Title = input.Title,
            Domain = input.Domain,
            Priority = priority,
            Notes = input.Notes,
            Tags = tags.ToList(),
            DueAt = dueAt,
        };

        var sourceText = SourceQuote.Clamp(input.SourceText);
        var rows = new List<ClarificationDocument>();

        foreach (var question in asked)
        {
            var clarification = new ClarificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                TaskId = task.Id,
                Status = ClarificationVocabulary.Open,

                // Every row describes the SAME matter, so they share one draft. What
                // differs between them is the question and the answers it offers.
                Draft = NewDraft(),
                Question = question.Question,

                // Null on a model-written question, which is already in the language
                // of the message it answers. Present on one this server composed, so
                // the card renders in the app's language instead of the English the
                // generator happens to be written in.
                QuestionKey = question.QuestionKey,
                QuestionParams = question.QuestionParams?.ToDictionary(e => e.Key, e => e.Value),
                Kind = question.Kind,
                CostOfWrong = question.CostOfWrong,
                Options = question.Options
                    .Select(o => new ClarificationOptionDocument
                    {
                        Label = o.Label,
                        DueAt = o.DueAt,
                        Title = o.Title,
                        Notes = o.Notes,
                        LabelKey = o.LabelKey,
                        LabelParams = o.LabelParams?.ToDictionary(e => e.Key, e => e.Value),
                    })
                    .ToList(),
                SourceText = sourceText,

                // No sourceKey. That is the VOICE lane's note-scoped idempotency key, and
                // the partial unique index is keyed on its presence; a chat-born hold is a
                // fresh create every time, which is what the open cap above exists to bound.
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _clarifications.InsertAsync(clarification, cancellationToken).ConfigureAwait(false);
            rows.Add(clarification);
        }

        return new HoldOutcome(task, rows[0], QueueFull: false, rows);
    }

    /// <summary><c>AskForTheAmount</c>'s key — how a money gap is recognised again.</summary>
    private const string AskHowMuchKey = "ask.howMuch";

    /// <summary>
    /// The model's questions made answerable, plus the gaps it did not notice.
    ///
    /// <para>
    /// <b>Deliberately <see cref="VoiceAutoFilePolicy.GapsFor"/> and not a fourth
    /// implementation.</b> Voice asks these questions, <c>POST /me/tasks</c> asks them
    /// through <see cref="MatterGapService"/>, and chat used to demand the model ask
    /// them — which is why chat was the only lane that could lose a matter. One
    /// generator means the three lanes cannot drift, and a chip added for voice shows
    /// up in chat the same day.
    /// </para>
    ///
    /// <para>
    /// Nothing here can fail the hold. A gap that cannot be turned into a question is
    /// simply not asked; the task is already on its way to being written either way.
    /// </para>
    /// </summary>
    private static List<HoldQuestion> WithServerGaps(
        List<HoldQuestion> questions,
        HoldInput input,
        DateTime? dueAt,
        string priority)
    {
        // Confidence 1 and no conflicts: the gap tests read neither, and a hold has
        // no score to report. Kind is unset for the same reason — it is decided
        // above, from costOfWrong, and is not an input to which gaps exist.
        var draft = new TaskDraft(
            input.Title,
            input.Domain,
            priority,
            Kind: null,
            DueAt: dueAt,
            input.Notes,
            SourceType: "chat",
            Confidence: 1,
            Conflicts: Array.Empty<PlanningConflict>(),
            TimeAssumed: false,
            Amount: input.Amount);

        var gaps = VoiceAutoFilePolicy.GapsFor(
            draft,
            VoiceAutoFilePolicy.ResolveZone(input.Timezone),
            timeAssumed: false);

        if (gaps.Count == 0)
        {
            return questions;
        }

        var dateGap = gaps.FirstOrDefault(g => string.Equals(g.Kind, "date", StringComparison.Ordinal));
        var amountGap = gaps.FirstOrDefault(g => g.QuestionKey == AskHowMuchKey);

        var filled = new List<HoldQuestion>(questions.Count + gaps.Count);
        var askedForADate = false;

        foreach (var question in questions)
        {
            var isDate = string.Equals(question.Kind, "date", StringComparison.Ordinal);
            askedForADate |= isDate;

            // A bare "when?" over a matter with no date. The model keeps its own
            // wording — it is in the language of the message it is answering — and
            // gets the server's pre-resolved chips, so a tap sets a real dueAt.
            if (isDate && question.Options.Count == 0 && !dueAt.HasValue && dateGap is not null)
            {
                filled.Add(question with { Options = ToHoldOptions(dateGap.Options) });
                continue;
            }

            filled.Add(question);
        }

        // Held for some other reason, with no date and nobody asking about it.
        if (!askedForADate && dateGap is not null)
        {
            filled.Add(ToHoldQuestion(dateGap));
        }

        // A bill has a figure. `NeedsAnAmount` keys on the domain AND the title, so a
        // renewal — "هجدد رخصة العربية" is `car`, never `finance` — is asked about
        // too.
        //
        // Skipped when the model already asked. The prompt still tells it to put the
        // figure in `secondary_question`, and it usually obeys: adding ours on top
        // produced the card that had "ما هو مبلغ الفاتورة؟" and "How much is …?" one
        // above the other, the same gap asked twice in two languages. Which of the
        // two lands is not worth arbitrating — the model's is in the language of the
        // message, so when it asks, it wins.
        if (amountGap is not null && !filled.Any(AlreadyAsksForMoney))
        {
            filled.Add(ToHoldQuestion(amountGap));
        }

        return filled;
    }

    /// <summary>
    /// Whether this question is already asking what the matter costs.
    ///
    /// <para>
    /// Read on <c>detail</c> questions only, and that is what makes it safe. The
    /// money words also appear in the DATE question on the same matter — "متى تدفع
    /// الفاتورة؟" carries فاتورة — and matching there would suppress the figure on
    /// every bill that was asked when it is due. A date question is kind 'date'; the
    /// figure is always kind 'detail' with no options, which is the shape the prompt
    /// mandates for it.
    /// </para>
    ///
    /// <para>
    /// Deliberately NOT <c>VoiceAutoFilePolicy</c>'s <c>MoneyWords</c>. That answers
    /// "is this matter about money", which is true of the whole card; this answers
    /// "is this sentence asking for a figure", which is true of one row on it.
    /// </para>
    /// </summary>
    private static bool AlreadyAsksForMoney(HoldQuestion question) =>
        question.QuestionKey == AskHowMuchKey
        || (string.Equals(question.Kind, "detail", StringComparison.Ordinal)
            && MoneyQuestionWords.IsMatch(question.Question));

    private static readonly Regex MoneyQuestionWords = new(
        @"how much|\bamount\b|\bcost(s)?\b|\bprice\b|\btotal\b"
        + @"|مبلغ|قيمة|تكلفة|سعر|بكام|كام",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static HoldQuestion ToHoldQuestion(DraftClarification gap) =>
        new(
            gap.Question,
            gap.Kind,
            gap.CostOfWrong,
            ToHoldOptions(gap.Options),
            gap.QuestionKey,
            gap.QuestionParams);

    private static List<HoldOption> ToHoldOptions(IReadOnlyList<DraftClarifyOption> options) =>
        options
            .Select(o => new HoldOption(o.Label, o.DueAt, Title: null, Notes: null, o.LabelKey, o.LabelParams))
            .ToList();

    /// <summary>
    /// The questions to raise, resolved against the top-level defaults.
    ///
    /// <para>
    /// An empty <c>questions</c> array is the legacy payload: one row, straight off
    /// the top-level fields. Anything else is the list, and each entry inherits
    /// whatever it left out — <c>options</c> included, but only when the KEY was
    /// absent. A <c>detail</c> gap riding alongside a date question sends an explicit
    /// empty array so the user types the answer rather than tapping a time chip.
    /// </para>
    /// </summary>
    private static List<HoldQuestion> BuildQuestions(
        HoldInput input,
        IReadOnlyList<HoldOption> options,
        string costOfWrong)
    {
        if (input.Questions is not { Count: > 0 } supplied)
        {
            return new List<HoldQuestion> { new(input.Question, input.Kind, costOfWrong, options) };
        }

        return supplied
            .Select(q => new HoldQuestion(
                q.Question,
                q.Kind ?? input.Kind,
                q.CostOfWrong ?? costOfWrong,
                q.Options is null ? options : Normalize(q.Options, input.Timezone)))
            .ToList();
    }

    /// <summary>
    /// Which questions actually get asked when the queue has room for only some.
    ///
    /// <para>
    /// <b>The DATE question survives first.</b> It is the only one that can unblock
    /// the task: a held matter sits at <c>kind:'list'</c> until a confirmed date
    /// promotes it, and that promotion happens when the date question is answered.
    /// Truncating in plain array order can therefore keep a <c>detail</c> gap and
    /// drop the date — leaving a reminder that is withheld forever with nothing left
    /// to ask that would release it. Which is this feature's own bug, one level up.
    /// </para>
    ///
    /// <para>
    /// Order is otherwise preserved, including among the survivors: the card stack
    /// reads them in the order they were asked, and the model puts the
    /// deadline-defining question first anyway, so nothing moves in the common case.
    /// </para>
    /// </summary>
    private static List<HoldQuestion> Prioritize(List<HoldQuestion> questions, int capacity)
    {
        if (questions.Count <= capacity)
        {
            return questions;
        }

        return questions
            .Select((question, index) => (question, index))
            .OrderBy(entry => entry.question.Kind == "date" ? 0 : 1)
            .ThenBy(entry => entry.index)
            .Take(capacity)
            .OrderBy(entry => entry.index)
            .Select(entry => entry.question)
            .ToList();
    }

    private static List<HoldOption> Normalize(IReadOnlyList<HoldRawOption> options, string? timezone) =>
        options
            .Select(o => new HoldOption(
                o.Label,
                o.DueAt is null ? null : HoldTimeNormalizer.Normalize(o.DueAt, timezone),
                o.Title,
                o.Notes))
            .ToList();

    /// <summary>
    /// How many more open questions this user may be given —
    /// <c>isAtOpenClarificationCap</c> as a REMAINDER, because a hold can now file
    /// more than one row and each occupies a slot.
    ///
    /// <para>
    /// <b>Deliberately not <c>VisibleOpen()</c>.</b> Every surface that LISTS or COUNTS
    /// held items for display must compose that predicate, and this is neither: a
    /// question the user skipped is still queued and still comes back, so it still
    /// occupies a slot. Counting only the currently-visible ones would let a user with
    /// twelve deferred questions accumulate twelve more.
    /// </para>
    ///
    /// <para>
    /// <b>Check-then-insert, with no transaction.</b> The count and the writes are
    /// separate operations, so two concurrent holds for one user can both read the
    /// same remainder and overshoot the cap. That race predates this change; what
    /// grew is its size, from at most one extra row per collision to at most three.
    /// Left alone deliberately: nothing in this codebase opens a Mongo session, the
    /// cap is backpressure rather than an invariant, and a user who briefly holds
    /// fourteen open questions is in no way harmed — the next hold simply files no
    /// question at all.
    /// </para>
    /// </summary>
    private async Task<int> CapacityAsync(ObjectId userId, CancellationToken cancellationToken)
    {
        var open = await _clarifications.CountOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var remaining = MaxOpenClarifications - open;
        return remaining <= 0 ? 0 : (int)remaining;
    }
}

/// <summary>One question, every default already resolved.</summary>
internal readonly record struct HoldQuestion(
    string Question,
    string Kind,
    string CostOfWrong,
    IReadOnlyList<HoldOption> Options,
    string? QuestionKey = null,
    IReadOnlyDictionary<string, string>? QuestionParams = null);
