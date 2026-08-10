using Life_Admin_Autopilot.BLL.Features.Clarifications;
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
/// None of the four routes is rate limited, and only the list reads a query
/// parameter. There is no query SCHEMA, so unknown parameters are ignored rather
/// than rejected — <c>QueryReader</c> is deliberately absent, exactly as in
/// <c>me.notifications</c>.
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

        // POST /me/clarifications/{id}/resolve — answer a held item; patches the Task.
        endpoints.MapPost("/me/clarifications/{id}/resolve", async (
            string id,
            HttpContext context,
            ClarificationRepository clarifications,
            TaskRepository tasks,
            ClarificationTaskUpdater updater,
            AiAvailability ai,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var doc = await LoadAsync(clarifications, caller.Id, id, cancellationToken).ConfigureAwait(false);

            // Idempotent: already answered or dropped (double-tap, stale client) →
            // echo the current state rather than creating a second task. Note this
            // short-circuits BEFORE the body is parsed, so a closed row answers 200
            // even for a payload that would otherwise be a 400.
            if (doc.Status != "open")
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
            var existing = doc.TaskId == ObjectId.Empty
                ? null
                : await tasks.FindLiveAsync(caller.Id, doc.TaskId, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                await clarifications
                    .CloseOutAsync(
                        doc,
                        new ClarificationPatch(Status: "dropped", ResolvedAt: now),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new ClarificationResolveResponse
                {
                    Clarification = doc.ToDto(),
                    Task = null,
                });
            }

            var (patch, answerLabel) = BuildPatch(doc, answer, ai);

            var task = await updater
                .RunUpdateAsync(caller.Id, doc.TaskId, patch, now, cancellationToken)
                .ConfigureAwait(false);

            await clarifications
                .CloseOutAsync(
                    doc,
                    new ClarificationPatch(Status: "resolved", Answer: answerLabel, ResolvedAt: now),
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            // Node records one AI message against the daily quota here, best-effort
            // and never gated. Only the custom branch does so, and that branch cannot
            // be reached while GEMINI_API_KEY is empty — it 503s above.
            return Results.Ok(new ClarificationResolveResponse
            {
                Clarification = doc.ToDto(),
                Task = task.ToDto(),
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

            if (doc.Status == "open")
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

            if (doc.Status == "open")
            {
                var now = DateTime.UtcNow;
                await clarifications
                    .CloseOutAsync(
                        doc,
                        new ClarificationPatch(Status: "dropped", ResolvedAt: now),
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
        ResolveAnswer answer,
        AiAvailability ai)
    {
        if (answer.Kind == ResolveAnswerKind.Custom)
        {
            if (!ai.IsConfigured)
            {
                throw ClarificationAiUnavailable.CustomAnswer();
            }

            throw AiUnavailable.NotWiredHere("POST /me/clarifications/{id}/resolve {type:'custom'}");
        }

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
