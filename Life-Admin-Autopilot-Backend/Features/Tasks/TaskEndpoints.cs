using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// The Matters slice — the 25 non-AI-authoring operations of
/// <c>server/src/routes/me.tasks.ts</c>.
///
/// <para><b>Route order.</b> Every literal segment is registered before
/// <c>/me/tasks/{id}</c>. ASP.NET already ranks a literal above a parameter, so
/// this ordering is belt-and-braces — but it also documents the set of words that
/// can never be a task id: <c>counts</c>, <c>tags</c>, <c>trash</c>, <c>bulk</c>,
/// <c>undo</c>, <c>search</c>, <c>summarize</c>, <c>estimate-backlog</c>,
/// <c>categorize</c> and <c>translate</c>.</para>
///
/// <para><b>Rate limits.</b> <c>taskSummaryLimiter</c> is ONE shared bucket
/// (10/min) across summarize, estimate-backlog, categorize and translate — four
/// routes drawing on the same allowance. Applied by constant, never by literal:
/// a typo would silently mint a private bucket and quadruple the real limit.</para>
/// </summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTasksEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Literal-segment routes first.
        TaskListEndpoints.Map(endpoints);
        FinancialInsightsEndpoints.Map(endpoints);
        TaskTrashEndpoints.Map(endpoints);
        TaskBulkEndpoints.Map(endpoints);
        TaskAiEndpoints.Map(endpoints);

        // Then the parameterised ones.
        TaskCrudEndpoints.Map(endpoints);
        TaskSubtaskEndpoints.Map(endpoints);

        return endpoints;
    }

    /// <summary>
    /// Parse a <c>:id</c> path segment the way the Node route does — with its OWN
    /// <c>Types.ObjectId.isValid</c> check.
    ///
    /// <para>
    /// <b>Deliberately not <c>MongoRepositoryBase.ParseObjectId</c>.</b> That throws
    /// <c>ObjectIdCastException</c>, which the kernel middleware renders as
    /// <c>404 not_found</c> "Not found" — the Mongoose CastError shape. But these
    /// routes never reach Mongoose with a bad id: they check first and throw their
    /// own <c>task_not_found</c>. Both are 404, and the differ compares
    /// <c>error.code</c> and <c>error.message</c> literally, so the distinction is
    /// observable. Verified live.
    /// </para>
    /// </summary>
    public static ObjectId TaskId(string? raw)
    {
        if (!ObjectId.TryParse(raw, out var id))
        {
            throw TaskNotFound();
        }

        return id;
    }

    public static AppException TaskNotFound() =>
        AppException.NotFound("task_not_found", "Task no longer exists.");

    /// <summary>The shared 10/min summary bucket, by constant.</summary>
    internal static RouteHandlerBuilder SummaryLimited(this RouteHandlerBuilder builder) =>
        builder.RateLimited(KernelRateLimiters.TaskSummary);
}
