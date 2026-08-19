using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Clarifications.Binding;
using Life_Admin_Autopilot_Backend.Features.Tasks;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Clarifications;

/// <summary>
/// Ports <c>server/src/routes/me.clarifications.ts</c> — the OPEN held items
/// powering the home banner and the <c>/clarify</c> card stack.
///
/// <para>
/// None of the four ported routes is rate limited, and only the list reads a query
/// parameter. There is no query SCHEMA, so unknown parameters are ignored rather
/// than rejected — <c>QueryReader</c> is deliberately absent, exactly as in
/// <c>me.notifications</c>.
/// </para>
///
/// <para>
/// <b>A FIFTH route has no Node counterpart: <c>POST /me/clarifications</c>.</b> The
/// reference exposes GET, defer, drop and resolve and no create, because in Node a
/// clarification is only ever written IN-PROCESS — by
/// <c>toolRunner.runHoldForClarification</c> for chat and by the voice transcriber
/// for recordings. Both live inside the server, so no endpoint was ever needed. The
/// port's planning agent runs in Langflow, OUTSIDE the API, and its
/// <c>holdForClarification</c> tool could therefore create the task and nothing else:
/// the reply said "Filed. What time?" and <c>db.clarifications</c> gained no row, so
/// the question the model had just asked was unanswerable and no uncertainty card
/// ever appeared. This route is the missing write path. Recorded in
/// <c>docs/DIVERGENCES.md</c> §6; it adds no parity row, because Node has no
/// behaviour here to differ from.
/// </para>
///
/// <para>
/// <b>Resolve, defer and drop all echo the IN-MEMORY document</b> after mutating it.
/// See <see cref="ClarificationRepository.CloseOutAsync"/>: the response carries the
/// PRE-update <c>updatedAt</c> beside freshly-patched fields. It looks like a bug
/// and is the observed behaviour of both servers.
/// </para>
///
/// <para>
/// <b>KNOWN DELTA, kernel-owned — a LEGACY row with no <c>taskId</c>.</b> Node omits
/// the key entirely (Mongoose never serialises an unset optional); this port emits
/// <c>"taskId":"000000000000000000000000"</c>, because
/// <c>ClarificationDocument.TaskId</c> is a non-nullable <c>ObjectId</c> and
/// <c>ClarificationDto.TaskId</c> a non-nullable <c>string</c>. Every response that
/// includes such a row differs by that one key — the list, and resolve/defer/drop on
/// that row. Reported rather than fixed: both types and the mapper live under
/// <c>Kernel/</c>, and KERNEL.md §6 says to report instead of duplicating a kernel
/// transform into a slice. The fix is three lines — <c>ObjectId?</c> on the document,
/// <c>string?</c> plus <c>[JsonIgnore(WhenWritingNull)]</c> on the DTO, and
/// <c>ToIdOrNull()</c> in the mapper, the helper <c>NotificationDocument</c> already
/// uses for exactly this. The slice's own LOGIC already handles the null case: an
/// absent <c>taskId</c> closes the question out as <c>dropped</c> with
/// <c>task: null</c>, matching Node.
/// </para>
/// </summary>
public static class ClarificationEndpoints
{
    private const string NotFoundCode = "clarification_not_found";

    private const string NotFoundMessage = "That question is no longer here.";

    public static IEndpointRouteBuilder MapClarificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/clarifications — newest first, cursor-paginated on createdAt.
        endpoints.MapGet("/me/clarifications", async (
            HttpContext context,
            ClarificationRepository clarifications,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var now = DateTime.UtcNow;

            var docs = await clarifications
                .FindVisibleOpenPageAsync(caller.Id, ReadCursor(context), now, cancellationToken)
                .ConfigureAwait(false);

            // The +1 probe: over the page size means another page exists.
            var hasMore = docs.Count > ClarificationRepository.PageSize;
            var page = hasMore ? docs.Take(ClarificationRepository.PageSize).ToList() : docs;

            return Results.Ok(new ClarificationListResponse
            {
                Clarifications = page.Select(d => d.ToDto()).ToList(),
                HasMore = hasMore,
                NextCursor = hasMore ? page[^1].CreatedAt : null,
            });
        })
        .RequireAuthorization();

        // GET /me/clarifications/by-ids?ids=a,b,c — what became of these questions.
        //
        // The chat card asks this on every mount, and it exists because the card
        // used to know only what happened while it was on screen. Answer a question,
        // reopen the conversation tomorrow, and the transcript re-rendered the hold
        // from the tool call in history — options untapped, Save armed, no trace of
        // the answer. The row was resolved server-side the whole time; nothing had
        // ever asked.
        //
        // Not served by the list endpoint: that returns VISIBLE OPEN rows, so a row
        // absent from it may be resolved, dropped, or deferred, and those must not
        // render the same way. Reading by id is the only read that distinguishes
        // them.
        endpoints.MapGet("/me/clarifications/by-ids", async (
            HttpContext context,
            ClarificationRepository clarifications,
            TaskRepository tasks,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // Bounded by the same cap that bounds the open queue: a transcript can
            // hold more holds than that across its whole history, but a card asking
            // about more rows than the user could ever have open is asking on
            // someone else's behalf.
            var ids = (context.Request.Query["ids"].ToString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(ClarificationHoldService.MaxOpenClarifications * 4)
                .Select(raw => ObjectId.TryParse(raw, out var parsed) ? parsed : (ObjectId?)null)
                // An unparseable id is a row that cannot exist, which is the same
                // outcome as one that does not: omitted from the response. It is not
                // a 400 — a transcript carrying one bad id would lose every good one.
                .Where(parsed => parsed.HasValue)
                .Select(parsed => parsed!.Value)
                .ToList();

            var docs = await clarifications
                .FindOwnedManyAsync(caller.Id, ids, cancellationToken)
                .ConfigureAwait(false);

            var rows = new List<ClarificationStatusDto>(docs.Count);
            foreach (var doc in docs)
            {
                // Only for a row that was actually answered. An open question's task
                // is the guess the card is still asking about, and the card already
                // has that from the tool call in history.
                TaskDocument? task = null;
                if (doc.Status == ClarificationVocabulary.Resolved
                    && doc.TaskId is { } linked
                    && linked != ObjectId.Empty)
                {
                    task = await tasks.FindLiveAsync(caller.Id, linked, cancellationToken).ConfigureAwait(false);
                }

                rows.Add(new ClarificationStatusDto
                {
                    Clarification = doc.ToDto(),
                    Task = task?.ToDto(),
                });
            }

            return Results.Ok(new ClarificationStatusResponse { Clarifications = rows });
        })
        .RequireAuthorization();

        // POST /me/clarifications — file an uncertain item AND the questions about it.
        //
        // ONE task, one row per question. `questions` (max 3) is optional and
        // additive: without it the top-level question/kind/options describe the one
        // row, exactly as before. With it, each entry is a gap the user answers on
        // its own — because a matter with two gaps folded into one sentence has one
        // answer slot, and the second gap disappears the moment the first is tapped.
        //
        // NO NODE COUNTERPART. See the class summary and docs/DIVERGENCES.md §6.
        endpoints.MapPost("/me/clarifications", async (
            HttpContext context,
            ClarificationHoldService holds,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<HoldBody>(
                    context,
                    KernelBodyOptions.Lenient(HoldBinder.Message, HoldBinder.Code),
                    cancellationToken)
                .ConfigureAwait(false);

            var outcome = await holds
                .HoldAsync(caller.Id, HoldBinder.Parse(body), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // INVARIANT: a hold either raised a question, or says WHY it did not.
            //
            // The chat card reads this response to decide whether the turn asked the
            // user anything. It can only be right if "no receipts" always comes with a
            // reason, because a success carrying neither is indistinguishable from a
            // question that simply failed to persist — and the card's only safe reading
            // of that is to invent a question from the arguments, which is the
            // 2026-08-17 phantom-questions incident.
            //
            // `queueFull` is the one reason that exists today. A second decline path —
            // a per-matter cap, quiet hours, a dedupe that suppresses a repeat — must
            // ship its own flag rather than silently returning nothing, and this is
            // where forgetting that becomes loud instead of becoming a phantom card.
            if (outcome.Rows.Count == 0 && !outcome.QueueFull)
            {
                throw new InvalidOperationException(
                    "Hold raised no question and gave no reason. Every decline path must "
                    + "report itself: the chat cannot tell a silent decline from a lost "
                    + "question, and renders the second as a question nobody was asked.");
            }

            return Results.Created((string?)null, new ClarificationCreateResponse
            {
                Clarification = outcome.Clarification?.ToDto(),
                Clarifications = outcome.Rows.Select(row => row.ToDto()).ToList(),
                Task = outcome.Task.ToDto(),
                QueueFull = outcome.QueueFull,
            });
        })
        .RequireAuthorization();

        // POST /me/clarifications/{id}/resolve — answer a held item; patches the Task.
        endpoints.MapPost("/me/clarifications/{id}/resolve", async (
            string id,
            HttpContext context,
            ClarificationRepository clarifications,
            TaskRepository tasks,
            ClarificationTaskUpdater updater,
            CustomAnswerInterpreter interpreter,
            ConflictService conflicts,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var doc = await LoadAsync(clarifications, caller.Id, id, cancellationToken).ConfigureAwait(false);

            // Idempotent: already answered or dropped (double-tap, stale client) →
            // echo the current state rather than creating a second task. Note this
            // short-circuits BEFORE the body is parsed, so a closed row answers 200
            // even for a payload that would otherwise be a 400.
            if (doc.Status != ClarificationVocabulary.Open)
            {
                return Results.Ok(new ClarificationResolveResponse
                {
                    Clarification = doc.ToDto(),
                    Task = null,
                });
            }

            var body = await KernelBody
                .ReadAsync<ResolveBody>(
                    context,
                    KernelBodyOptions.Lenient(ResolveAnswerBinder.Message, ResolveAnswerBinder.Code),
                    cancellationToken)
                .ConfigureAwait(false);

            var answer = ResolveAnswerBinder.Parse(body);

            var now = DateTime.UtcNow;

            // The task already exists — it was created when the question was raised.
            // If it has since been deleted the question is moot: close it out rather
            // than resurrecting work the user threw away.
            // A legacy row predating the required constraint has no taskId at all,
            // which now reads as null rather than a zero id. Both are treated as
            // "no task": close the question out instead of resurrecting work.
            var taskId = doc.TaskId is { } linked && linked != ObjectId.Empty ? linked : (ObjectId?)null;

            var existing = taskId is null
                ? null
                : await tasks.FindLiveAsync(caller.Id, taskId.Value, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                await clarifications
                    .CloseOutAsync(
                        doc,
                        new ClarificationPatch(Status: ClarificationVocabulary.Dropped, ResolvedAt: now),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new ClarificationResolveResponse
                {
                    Clarification = doc.ToDto(),
                    Task = null,
                });
            }

            ClarificationTaskPatch patch;
            string answerLabel;
            if (answer.Kind == ResolveAnswerKind.Custom)
            {
                // The seam is the Gemini-direct planning one; its key is the gate.
                // AiAvailability answers for GEMINI_API_KEY, which this deployment
                // deliberately leaves empty — see CustomAnswerInterpreter's doc.
                if (!interpreter.IsConfigured)
                {
                    throw ClarificationAiUnavailable.CustomAnswer();
                }

                // A 502 (no usable function call) or 400 (invalid args) escapes HERE,
                // before the close-out below — the question must stay open rather
                // than losing the held item to an answer nobody could interpret.
                patch = await interpreter
                    .InterpretAsync(doc, answer.Text, answer.Timezone, now, cancellationToken)
                    .ConfigureAwait(false);

                // Node records the raw typed text as the answer — never a paraphrase.
                answerLabel = answer.Text;
            }
            else
            {
                (patch, answerLabel) = BuildPatch(doc, answer);
            }

            // Non-null here: a null taskId returned above via the `existing is null` path.
            var task = await updater
                .RunUpdateAsync(caller.Id, taskId!.Value, patch, now, cancellationToken)
                .ConfigureAwait(false);

            await clarifications
                .CloseOutAsync(
                    doc,
                    new ClarificationPatch(Status: ClarificationVocabulary.Resolved, Answer: answerLabel, ResolvedAt: now),
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            // Did the answer put it on top of something? Asked AFTER the close-out,
            // deliberately: the question has been answered either way, and a check
            // that failed must not be able to hold a resolved item open.
            var clash = await RecheckAsync(
                    conflicts,
                    caller.Id,
                    task,
                    patch.DueAt,
                    answer.Timezone,
                    cancellationToken)
                .ConfigureAwait(false);

            // Node records one AI message against the daily quota here, best-effort
            // and never gated. Only the custom branch does so, and that branch cannot
            // be reached while GEMINI_API_KEY is empty — it 503s above.
            return Results.Ok(new ClarificationResolveResponse
            {
                Clarification = doc.ToDto(),
                Task = task.ToDto(),
                Conflicts = clash.Conflicts,
                Suggestions = clash.Suggestions,
                SuggestionReason = clash.Reason,
            });
        })
        .RequireAuthorization();

        // POST /me/clarifications/{id}/defer — "not now". The card stack's Skip.
        //
        // Skip used to be purely local (it advanced an index and made no request), so
        // the server never learned the user had passed and served the item again
        // identically next session.
        endpoints.MapPost("/me/clarifications/{id}/defer", async (
            string id,
            HttpContext context,
            ClarificationRepository clarifications,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var doc = await LoadAsync(clarifications, caller.Id, id, cancellationToken).ConfigureAwait(false);

            if (doc.Status == ClarificationVocabulary.Open)
            {
                var now = DateTime.UtcNow;

                // Status is NOT changed — the row stays `open` and simply drops out of
                // VisibleOpen() until the window passes.
                await clarifications
                    .CloseOutAsync(
                        doc,
                        new ClarificationPatch(DeferredUntil: now + ClarificationRepository.DeferWindow),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Results.Ok(new ClarificationEnvelope { Clarification = doc.ToDto() });
        })
        .RequireAuthorization();

        // POST /me/clarifications/{id}/drop — discard without creating anything.
        endpoints.MapPost("/me/clarifications/{id}/drop", async (
            string id,
            HttpContext context,
            ClarificationRepository clarifications,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var doc = await LoadAsync(clarifications, caller.Id, id, cancellationToken).ConfigureAwait(false);

            if (doc.Status == ClarificationVocabulary.Open)
            {
                var now = DateTime.UtcNow;
                await clarifications
                    .CloseOutAsync(
                        doc,
                        new ClarificationPatch(Status: ClarificationVocabulary.Dropped, ResolvedAt: now),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Results.Ok(new ClarificationEnvelope { Clarification = doc.ToDto() });
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// The id check and the ownership read, which every write route runs in that
    /// order and answers with the SAME 404.
    ///
    /// <para>
    /// Deliberately not <c>MongoRepositoryBase.ParseObjectId</c>: that raises the
    /// kernel's generic <c>not_found</c>, and these routes hand-throw
    /// <c>clarification_not_found</c> with their own message.
    /// </para>
    /// </summary>
    private static async Task<ClarificationDocument> LoadAsync(
        ClarificationRepository clarifications,
        ObjectId userId,
        string id,
        CancellationToken cancellationToken)
    {
        // `Types.ObjectId.isValid` also accepts any 12-CHARACTER string, casting its
        // bytes to an id — but such a row can never exist, so Node's next step 404s
        // with this identical error. Rejecting here is the same observable outcome.
        if (!ObjectId.TryParse(id, out var parsed))
        {
            throw AppException.NotFound(NotFoundCode, NotFoundMessage);
        }

        return await clarifications.FindOwnedAsync(userId, parsed, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound(NotFoundCode, NotFoundMessage);
    }

    /// <summary>
    /// The two answer branches. Returns the task patch and the label recorded on the
    /// clarification's <c>answer</c>.
    /// </summary>
    private static (ClarificationTaskPatch Patch, string AnswerLabel) BuildPatch(
        ClarificationDocument doc,
        ResolveAnswer answer)
    {

        // `doc.options[index]` — an index inside the schema's 0..3 but past the end
        // of the stored array is this 400, NOT the schema's `invalid_answer`.
        if (answer.Index >= doc.Options.Count)
        {
            throw AppException.BadRequest("invalid_option", "That answer is no longer available.");
        }

        var option = doc.Options[answer.Index];

        // Node spreads each field only when TRUTHY, so an empty-string title or note
        // never reaches the patch.
        return (
            new ClarificationTaskPatch(
                Title: string.IsNullOrEmpty(option.Title) ? null : option.Title,
                Notes: string.IsNullOrEmpty(option.Notes) ? null : option.Notes,
                DueAt: option.DueAt ?? doc.Draft.DueAt,
                // A confirmed date is the whole point of asking: a task whose reminder
                // was withheld on an uncertain high-stakes date becomes a real
                // reminder now.
                Kind: (option.DueAt ?? doc.Draft.DueAt).HasValue ? "reminder" : null),
            option.Label);
    }

    /// <summary>
    /// <c>req.query.before</c> — the <c>createdAt</c> of the last item seen.
    ///
    /// <para>
    /// Node reads it only when <c>typeof … === 'string'</c>, so a REPEATED parameter
    /// (which express hands over as an array) is ignored. An unparsable value is
    /// <c>NaN</c> and also ignored — verified live: <c>?before=garbage</c> answers
    /// 200 with the full first page, never a 400.
    /// </para>
    /// </summary>
    private static DateTime? ReadCursor(HttpContext context)
    {
        var raw = context.Request.Query["before"];
        if (raw.Count != 1)
        {
            return null;
        }

        return JsDate.TryParse(raw[0], out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Does the answered task now clash with something, and where could it go
    /// instead?
    ///
    /// <para>
    /// <b>Only when the answer moved the date.</b> A confirmation ("yes, that is
    /// right") changes nothing about when the matter is, so re-checking it would
    /// spend a pool read to re-report a clash the user was already told about when
    /// the item was filed — and, worse, would put a warning on a card whose answer
    /// was not about time at all.
    /// </para>
    ///
    /// <para>
    /// <b>Excludes the task itself</b>, which is the whole reason this cannot reuse
    /// the draft-shaped check: the matter now EXISTS at the instant being tested, so
    /// a check that did not exclude it would find it colliding with itself, every
    /// time, and every answer would come back with a conflict.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> The answer has already been written and the question is
    /// already closed by the time this runs. A failed check means the client shows no
    /// warning — the same as before this existed — and that is strictly better than a
    /// 500 on a request whose real work succeeded.
    /// </para>
    /// </summary>
    private static async Task<RecheckResult> RecheckAsync(
        ConflictService conflicts,
        ObjectId userId,
        TaskDocument task,
        DateTime? answeredDueAt,
        string? timezone,
        CancellationToken cancellationToken)
    {
        if (answeredDueAt is null || task.DueAt is not { } dueAt)
        {
            return RecheckResult.None;
        }

        try
        {
            var candidate = ConflictService.MatterCandidate.From(task);
            var pool = await conflicts.OpenMattersAsync(userId, cancellationToken).ConfigureAwait(false);

            var found = await conflicts
                .CheckAsync(
                    userId,
                    candidate,
                    dueAt,
                    pool,
                    excludeTaskId: task.Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (found.Count == 0)
            {
                return RecheckResult.None;
            }

            var offset = OffsetFor(timezone, dueAt);
            var suggestions = SlotSuggester.Suggest(
                task.Title ?? string.Empty,
                dueAt,
                offset,
                at => ConflictService.ClashesWithin(at, candidate, pool, task.Id));

            return new RecheckResult(
                found
                    .Select(c => new ClarificationConflictDto
                    {
                        TaskId = c.TaskId.ToString(),
                        Title = c.Title,
                        DueAt = c.DueAt,
                        Kind = c.Kind,
                        Reason = c.Reason,
                    })
                    .ToList(),
                suggestions,
                SlotSuggester.ReasonFor(task.Title ?? string.Empty));
        }
        catch (Exception)
        {
            return RecheckResult.None;
        }
    }

    /// <summary>The user's offset at that instant, so a suggested "evening" is theirs.</summary>
    private static TimeSpan OffsetFor(string? timezone, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeSpan.Zero;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone).GetUtcOffset(at);
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private readonly record struct RecheckResult(
        IReadOnlyList<ClarificationConflictDto> Conflicts,
        IReadOnlyList<DateTime> Suggestions,
        string Reason)
    {
        public static RecheckResult None => new(
            Array.Empty<ClarificationConflictDto>(),
            Array.Empty<DateTime>(),
            string.Empty);
    }
}

/// <summary>
/// The SIXTH distinct <c>ai_not_configured</c> message in the API. The other five
/// belong to Matters (<see cref="AiUnavailable"/>); this one is the clarifications
/// slice's, and the differ compares the string literally.
/// </summary>
public static class ClarificationAiUnavailable
{
    /// <summary><c>{type: 'custom'}</c> needs one bounded model call to interpret the text.</summary>
    public static AppException CustomAnswer() =>
        new(
            503,
            AiUnavailable.Code,
            "Typing your own answer needs AI configured. Pick one of the suggestions instead.");
}
