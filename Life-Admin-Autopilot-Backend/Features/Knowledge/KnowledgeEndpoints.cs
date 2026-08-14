using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Knowledge;

/// <summary>
/// <c>POST /me/knowledge/search</c> — the Knowledge Agent's retrieval tool.
///
/// <para>
/// Authenticated and owner-scoped: the caller's own token decides whose corpus is
/// searched, and there is no userId in the body to spoof. That is deliberate — the
/// deleted <c>UserTasksTestController</c> took its userId from the caller and is
/// exactly the IDOR this route must not reintroduce.
/// </para>
/// </summary>
public static class KnowledgeEndpoints
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;

    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/me/knowledge/search", async (
            HttpContext context,
            KnowledgeService knowledge,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<KnowledgeSearchBody>(
                    context,
                    KernelBodyOptions.Lenient("Request validation failed"),
                    cancellationToken)
                .ConfigureAwait(false);

            var query = body.Query?.Trim() ?? string.Empty;
            var limit = Math.Clamp(body.Limit ?? DefaultLimit, 1, MaxLimit);

            // Availability before payload checks, matching the AI shell's ordering.
            if (!knowledge.IsConfigured)
            {
                // 503 has no factory on AppException — the AI slice builds its own the
                // same way (see AiShellErrors), because each 503 message is route-specific.
                throw new AppException(
                    503,
                    "knowledge_not_configured",
                    "Retrieval is not configured. Set EMBEDDINGS_API_KEY to enable it.");
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                throw AppException.BadRequest("invalid_query", "A non-empty 'query' is required.");
            }

            try
            {
                var matches = await knowledge
                    .SearchAsync(caller.Id, query, limit, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new
                {
                    ok = true,
                    count = matches.Count,
                    matches = matches.Select(m => new
                    {
                        id = m.Chunk.Id.ToString(),
                        sourceType = m.Chunk.SourceType,
                        sourceId = m.Chunk.SourceId.ToString(),
                        chunkIndex = m.Chunk.ChunkIndex,
                        text = m.Chunk.Text,
                        score = m.Score,
                    }),
                });
            }
            catch (VectorSearchUnavailableException ex)
            {
                // A cluster without $vectorSearch is an operator problem, not a bad
                // request — say so rather than returning an empty result the agent
                // would report to the user as "you have nothing about that".
                throw new AppException(503, "vector_search_unavailable", ex.Message);
            }
        })
        .RequireAuthorization();

        // ---- GET /me/briefing/today — the Knowledge Agent's daily briefing ----
        endpoints.MapGet("/me/briefing/today", async (
            HttpContext context,
            KnowledgeAgentService agent,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var timezone = context.Request.Query["timezone"].ToString();

            var briefing = await agent
                .BriefAsync(caller.Id, string.IsNullOrWhiteSpace(timezone) ? null : timezone, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                headline = briefing.Headline,
                summary = briefing.Summary,
                // Lets the UI mark a fallback sentence as such rather than passing a
                // deterministic string off as the assistant's own words.
                phrased = briefing.Phrased,
                items = briefing.Items.Select(i => new
                {
                    taskId = i.TaskId.ToString(),
                    title = i.Title,
                    domain = i.Domain,
                    dueAt = i.DueAt,
                    overdue = i.Overdue,
                }),
                conflicts = briefing.Conflicts.Select(Conflict),
            });
        })
        .RequireAuthorization();

        // ---- GET /me/tasks/{id}/conflicts — re-check after an edit ------------
        endpoints.MapGet("/me/tasks/{id}/conflicts", async (
            string id,
            HttpContext context,
            KnowledgeAgentService agent,
            TaskRepository tasks,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            if (!ObjectId.TryParse(id, out var taskId))
            {
                throw AppException.NotFound("task_not_found", "Task not found.");
            }

            var task = await tasks.FindLiveAsync(caller.Id, taskId, cancellationToken).ConfigureAwait(false)
                ?? throw AppException.NotFound("task_not_found", "Task not found.");

            var conflicts = await agent.RecheckAsync(caller.Id, task, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                taskId = task.Id.ToString(),
                conflicts = conflicts.Select(Conflict),
            });
        })
        .RequireAuthorization();

        // ---- POST /me/conflicts — would this DRAFT clash? ---------------------
        //
        // The sibling below needs a task id, which a draft does not have: nothing is
        // saved yet, and requiring an id to ask the question is what forced the
        // capture flow to wait until Save to find out. This takes the proposed values
        // themselves, so the answer can be shown while the user is still choosing a
        // time rather than after they commit to one.
        endpoints.MapPost("/me/conflicts", async (
            HttpContext context,
            ConflictService conflicts,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var body = await KernelBody
                .ReadAsync<ConflictPreviewBody>(
                    context,
                    KernelBodyOptions.Lenient("Invalid conflict check payload."),
                    cancellationToken)
                .ConfigureAwait(false);

            var title = body.Title?.Trim() ?? string.Empty;
            if (title.Length == 0)
            {
                throw AppException.BadRequest("invalid_input", "A non-empty 'title' is required.");
            }

            var pool = await conflicts.OpenMattersAsync(caller.Id, cancellationToken).ConfigureAwait(false);
            var found = await conflicts
                .CheckAsync(caller.Id, title, body.DueAt, pool, excludeTaskId: null, cancellationToken)
                .ConfigureAwait(false);

            // A refusal the user cannot act on is just an obstacle. When there IS
            // a clash, offer times that suit this kind of matter and are already
            // known to be free — checked against the same pool, so a suggestion
            // cannot be refused the moment it is taken.
            var suggestions = Array.Empty<DateTime>();
            var reason = string.Empty;
            if (found.Count > 0 && body.DueAt is { } wanted)
            {
                var offset = OffsetFor(body.Timezone, wanted);
                suggestions = SlotSuggester
                    .Suggest(title, wanted, offset, at => ConflictService.ClashesWithin(at, pool, null))
                    .ToArray();
                reason = SlotSuggester.ReasonFor(title);
            }

            return Results.Ok(new
            {
                conflicts = found.Select(Conflict),
                suggestions,
                suggestionReason = reason,
            });
        })
        .RequireAuthorization();

        // ---- POST /me/tasks/{id}/conflicts — would this change clash? ---------
        //
        // The GET above answers "is this task, as saved, in conflict?" — which is a
        // question that can only be asked once the damage is done. This asks it of a
        // change that has NOT happened yet, so a caller can decline to make it.
        //
        // That gap is what let the chat agent move a matter onto another one and
        // report success: its updateTask tool patches immediately, and the clash only
        // surfaced later when the user happened to open the task and found a
        // "Scheduling clash" banner over a change they were never warned about.
        endpoints.MapPost("/me/tasks/{id}/conflicts", async (
            string id,
            HttpContext context,
            ConflictService conflicts,
            TaskRepository tasks,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            if (!ObjectId.TryParse(id, out var taskId))
            {
                throw AppException.NotFound("task_not_found", "Task not found.");
            }

            var body = await KernelBody
                .ReadAsync<ConflictPreviewBody>(
                    context,
                    KernelBodyOptions.Lenient("Invalid conflict check payload."),
                    cancellationToken)
                .ConfigureAwait(false);

            var task = await tasks.FindLiveAsync(caller.Id, taskId, cancellationToken).ConfigureAwait(false)
                ?? throw AppException.NotFound("task_not_found", "Task not found.");

            // A sparse patch: only the fields the caller intends to change are
            // supplied, and everything else is checked as it currently stands.
            var title = string.IsNullOrWhiteSpace(body.Title) ? task.Title : body.Title.Trim();
            var dueAt = body.DueAt ?? task.DueAt;

            var pool = await conflicts.OpenMattersAsync(caller.Id, cancellationToken).ConfigureAwait(false);

            // Excluded from its own pool — otherwise every task is a perfect duplicate
            // of itself, zero minutes away from itself.
            var found = await conflicts
                .CheckAsync(caller.Id, title, dueAt, pool, excludeTaskId: taskId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                taskId = task.Id.ToString(),
                conflicts = found.Select(Conflict),
            });
        })
        .RequireAuthorization();
    }

    /// <summary>
    /// The user's UTC offset at that moment — DST included, which is why it is
    /// resolved AT the instant rather than taken as a fixed number.
    /// </summary>
    private static TimeSpan OffsetFor(string? timezone, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeSpan.Zero;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone).GetUtcOffset(at);
        }
        catch (Exception)
        {
            // An unknown zone must not fail a conflict check; UTC just makes the
            // suggested hours less local, not wrong.
            return TimeSpan.Zero;
        }
    }

    private static object Conflict(MatterConflict c) => new
    {
        taskId = c.TaskId.ToString(),
        title = c.Title,
        dueAt = c.DueAt,
        kind = c.Kind,
        reason = c.Reason,
    };
}

/// <summary>
/// <c>{ dueAt?: ISO-8601, title?: string }</c> — the proposed change, not the task.
/// Both optional: an omitted field means "leave it as it is and check the rest".
/// </summary>
public sealed class ConflictPreviewBody
{
    [JsonPropertyName("dueAt")]
    public DateTime? DueAt { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>IANA zone. Without it "evening" would mean UTC's evening.</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}

/// <summary>
/// <c>{ query: string, limit?: number }</c>. Lenient like the rest of the API —
/// unknown keys are stripped rather than rejected.
/// </summary>
public sealed class KnowledgeSearchBody
{
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>Nullable so "absent" and "sent 0" stay distinguishable.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}
