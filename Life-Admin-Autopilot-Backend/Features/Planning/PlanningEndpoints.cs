using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;

namespace Life_Admin_Autopilot_Backend.Features.Planning;

/// <summary>
/// The two-phase entry point from the API doc and <c>ai_flow_V4</c>:
/// <c>POST /api/planning/propose</c> then <c>POST /api/planning/commit</c>.
///
/// <para>
/// <b>Propose never writes.</b> That is the whole point of the split, and the SRS is
/// explicit about why: conflict detection has to happen BEFORE anything is saved,
/// not after. The chat agent's tools write immediately, which is the behaviour this
/// pair exists to replace for the capture flow.
/// </para>
///
/// <para>
/// <b>Commit is per task, not per proposal.</b> One utterance can yield three drafts
/// ("go swimming, pay bills, and text someone"); the user confirms each one on its
/// own, so a user who confirms two of three and closes the app keeps those two.
/// </para>
/// </summary>
public static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // ---- POST /api/planning/propose — reads only ------------------------
        endpoints.MapPost("/api/planning/propose", async (
            HttpContext context,
            PlanningService planning,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<ProposeBody>(
                    context,
                    KernelBodyOptions.Lenient("Invalid proposal payload."),
                    cancellationToken)
                .ConfigureAwait(false);

            var text = body.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                throw AppException.BadRequest("invalid_input", "A non-empty 'text' is required.");
            }

            var drafts = await planning
                .ProposeAsync(caller.Id, text, body.Timezone, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                draftTasks = drafts.Select(d => new
                {
                    title = d.Title,
                    dueDate = d.DueAt,
                    category = d.Domain,
                    priority = d.Priority,
                    kind = d.Kind,
                    notes = d.Notes,
                    sourceType = d.SourceType,
                    confidence = d.Confidence,
                    // "the day is theirs, the clock time is ours" — the app asks for
                    // the real one rather than presenting the placeholder as chosen.
                    timeAssumed = d.TimeAssumed,
                    conflicts = d.Conflicts.Select(c => new
                    {
                        taskId = c.TaskId.ToString(),
                        title = c.Title,
                        dueAt = c.DueAt,
                        reason = c.Reason,
                    }),
                }),
            });
        }).RequireAuthorization();

        // ---- POST /api/planning/commit — writes one confirmed draft ---------
        endpoints.MapPost("/api/planning/commit", async (
            HttpContext context,
            TaskWriteService writes,
            ConflictService conflicts,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<CommitBody>(
                    context,
                    KernelBodyOptions.Lenient("Invalid commit payload."),
                    cancellationToken)
                .ConfigureAwait(false);

            var title = body.Title?.Trim() ?? string.Empty;
            if (title.Length == 0)
            {
                throw AppException.BadRequest("invalid_input", "A non-empty 'title' is required.");
            }

            // A captured matter must land on a date.
            //
            // Everything that reaches this endpoint came from Kitto interpreting speech
            // or a sentence, and a dated matter is the whole product — an undated one
            // never fires, never appears in a briefing, and is discovered months later.
            // So the capture flows ASK rather than filing something inert. The rule is
            // deliberately scoped to this endpoint: POST /me/tasks still accepts an
            // undated task, because a user who deliberately builds a list item has
            // chosen that, and inferring it from a half-heard sentence is not the same
            // act.
            if (body.DueDate is null)
            {
                throw AppException.BadRequest(
                    "date_required",
                    "This needs a date before it can be saved. Ask the user when it should happen.");
            }

            // A clash is refused HERE, not reported afterwards.
            //
            // Propose already returned the conflicts, so an honest client has shown
            // them and is sending confirmConflicts. But propose holds no server-side
            // state, so this endpoint cannot assume that happened — and the draft may
            // have been edited onto a clash after it was proposed, or minutes may have
            // passed and another matter landed there. Re-checking at the moment of the
            // write is the only check that is actually about what is being written.
            if (!body.ConfirmConflicts)
            {
                var pool = await conflicts.OpenMattersAsync(caller.Id, cancellationToken).ConfigureAwait(false);
                var found = await conflicts
                    .CheckAsync(
                        caller.Id,
                        new ConflictService.MatterCandidate(
                            title,
                            PlanningService.NormaliseDomain(body.Category),
                            PlanningService.NormalisePriority(body.Priority)),
                        body.DueDate,
                        pool,
                        excludeTaskId: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (found.Count > 0)
                {
                    throw ConflictRefusal.For(found);
                }
            }

            // The user may have edited the draft before confirming, so the committed
            // values come from the REQUEST, never from a cached proposal. That also
            // means propose holds no server-side state and nothing expires.
            var task = await writes
                .CreateAsync(
                    caller.Id,
                    new TaskCreateInput(
                        title,
                        PlanningService.NormaliseDomain(body.Category),
                        body.Kind,
                        PlanningService.NormalisePriority(body.Priority),
                        body.Tags,
                        body.DueDate,
                        body.Notes,
                        Estimate: null,
                        // Null until the agent's createTask tool learns to report a
                        // figure — the draft body has no amount to pass on yet, so
                        // there is nothing here to forward. Wiring the tool schema
                        // is the other half; this line is what it changes.
                        Amount: null,
                        SourceVoiceNoteId: null),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Results.Created((string?)null, new { task = task.ToDto() });
        }).RequireAuthorization();

        // ---- POST /me/tasks/draft — what the CHAT agent's createTask calls -----
        //
        // Shaped like POST /me/tasks so the Langflow tool component needed only its
        // path changed, but it writes NOTHING. The agent is told the matter is
        // awaiting confirmation, and the frontend renders the draft for the user to
        // confirm — which routes to /api/planning/commit like every other capture.
        //
        // This is what stops chat from being the one surface that still saves
        // silently: the tool it calls can no longer save.
        endpoints.MapPost("/me/tasks/draft", async (
            HttpContext context,
            ConflictService conflicts,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<DraftBody>(
                    context,
                    KernelBodyOptions.Lenient("Invalid draft payload."),
                    cancellationToken)
                .ConfigureAwait(false);

            var title = body.Title?.Trim() ?? string.Empty;
            if (title.Length == 0)
            {
                throw AppException.BadRequest("invalid_input", "A non-empty 'title' is required.");
            }

            // Two vocabularies meet here. The Langflow tool speaks the task API's
            // `domain`/`dueAt` because it was written against POST /me/tasks; the app
            // speaks the API doc's `category`/`dueDate`. Accepting both is what keeps
            // one endpoint serving both callers — and silently defaulting instead
            // produced a car service filed under `home` with its date dropped.
            var category = body.Category ?? body.Domain;
            var due = body.DueDate ?? body.DueAt;

            var pool = await conflicts.OpenMattersAsync(caller.Id, cancellationToken).ConfigureAwait(false);
            var found = await conflicts
                .CheckAsync(
                    caller.Id,
                    new ConflictService.MatterCandidate(
                        title,
                        PlanningService.NormaliseDomain(category),
                        PlanningService.NormalisePriority(body.Priority)),
                    due,
                    pool,
                    excludeTaskId: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                ok = true,
                // The word the agent reads back. Without it the model narrates
                // "Filed." and the user believes a matter exists that does not.
                status = "awaiting_confirmation",
                note = "Nothing has been saved. The user must confirm this draft.",
                draft = new
                {
                    title,
                    category = PlanningService.NormaliseDomain(category),
                    priority = PlanningService.NormalisePriority(body.Priority),
                    kind = body.Kind,
                    dueDate = due,
                    notes = body.Notes,
                    sourceType = "chat",
                    confidence = 1.0,
                    conflicts = found.Select(c => new
                    {
                        taskId = c.TaskId.ToString(),
                        title = c.Title,
                        dueAt = c.DueAt,
                        kind = c.Kind,
                        reason = c.Reason,
                    }),
                },
            });
        }).RequireAuthorization();
    }
}

/// <summary><c>{ text, timezone?, inputType? }</c>.</summary>
public sealed class ProposeBody
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>Accepted and echoed back on each draft; voice and text differ only here.</summary>
    [JsonPropertyName("inputType")]
    public string? InputType { get; set; }
}

/// <summary>
/// The draft endpoint's body. Extends <see cref="CommitBody"/> with the task
/// API's field names, because the Langflow tool was written against
/// <c>POST /me/tasks</c> and sends <c>domain</c>/<c>dueAt</c>.
/// </summary>
public sealed class DraftBody : CommitBody
{
    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("dueAt")]
    public DateTime? DueAt { get; set; }
}

/// <summary>
/// Refusing to write something the user has not been warned about.
///
/// <para>
/// <b>409, not 400.</b> The request is well-formed and the caller is allowed to make
/// it — the state of the user's calendar is what makes it wrong, and that is exactly
/// what 409 means. The distinction is load-bearing for the chat agent: a 400 reads
/// as "you built the call wrong" and it retries with different fields, while a 409
/// carrying the clashing matters reads as "ask the human", which is the behaviour
/// wanted.
/// </para>
///
/// <para>
/// The conflicts travel in <c>details</c> so the caller can name what it collided
/// with. A refusal the user cannot act on is just a failure.
/// </para>
/// </summary>
public static class ConflictRefusal
{
    public const string Code = "conflict_detected";

    public static AppException For(IReadOnlyList<MatterConflict> conflicts) => new(
        409,
        Code,
        conflicts.Count == 1
            ? "This clashes with something already scheduled. Confirm to save it anyway."
            : $"This clashes with {conflicts.Count} matters already scheduled. Confirm to save it anyway.",
        new
        {
            conflicts = conflicts.Select(c => new
            {
                taskId = c.TaskId.ToString(),
                title = c.Title,
                dueAt = c.DueAt,
                kind = c.Kind,
                reason = c.Reason,
            }),
        });
}

/// <summary>One confirmed draft, in the same vocabulary propose returned.</summary>
public class CommitBody
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// The user has SEEN the clash and still wants it saved.
    ///
    /// <para>
    /// Defaults to false, so a caller that has never heard of conflicts gets the safe
    /// behaviour rather than the silent one. It is a claim about what the user was
    /// shown, which is why it belongs to the client that did the showing and cannot
    /// be inferred here.
    /// </para>
    /// </summary>
    [JsonPropertyName("confirmConflicts")]
    public bool ConfirmConflicts { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("priority")]
    public string? Priority { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("dueDate")]
    public DateTime? DueDate { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}
