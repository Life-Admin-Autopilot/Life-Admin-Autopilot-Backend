using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Tasks.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// <c>GET /me/tasks</c>, <c>/counts</c> and <c>/tags</c> — the read surface.
/// </summary>
internal static class TaskListEndpoints
{
    /// <summary>
    /// The full query surface: 15 filters plus the three transport concerns. The
    /// filter half is <c>TaskFilterSchema</c>, shared with the chat agent and the
    /// NL search compiler, which is why it is bound into the kernel's
    /// <c>TaskQuery.TaskFilter</c> rather than reassembled here.
    /// </summary>
    private static readonly string[] ListKeys =
    {
        "q", "status", "domain", "priority", "kind", "tag",
        "dueBefore", "dueAfter", "createdBefore", "createdAfter", "completedBefore", "completedAfter",
        "overdue", "undated", "untagged",
        "sort", "limit", "cursor",
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me/tasks", async (
            HttpContext ctx,
            TaskRepository repository,
            UserLocaleReader locales,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            var q = new QueryReader(ctx.Request.Query, ListKeys);

            var filter = new TaskQuery.TaskFilter
            {
                Q = q.String("q", minLength: 1, maxLength: 200),
                Status = q.CsvEnum("status", TaskVocabulary.Statuses),
                Domain = q.CsvEnum("domain", TaskVocabulary.Domains),
                Priority = q.CsvEnum("priority", TaskVocabulary.Priorities),
                Kind = q.CsvEnum("kind", TaskVocabulary.Kinds),

                // `tag` is `z.string().max(400)` with NO minimum, so `?tag=` is a
                // valid empty string that simply filters nothing. QueryReader.Raw
                // would reject it, so read it directly.
                Tag = QueryStrings.Optional(ctx, q, "tag", maxLength: 400),

                DueBefore = QueryStrings.IsoInstant(ctx, q, "dueBefore"),
                DueAfter = QueryStrings.IsoInstant(ctx, q, "dueAfter"),
                CreatedBefore = QueryStrings.IsoInstant(ctx, q, "createdBefore"),
                CreatedAfter = QueryStrings.IsoInstant(ctx, q, "createdAfter"),
                CompletedBefore = QueryStrings.IsoInstant(ctx, q, "completedBefore"),
                CompletedAfter = QueryStrings.IsoInstant(ctx, q, "completedAfter"),

                Overdue = q.Bool("overdue"),
                Undated = q.Bool("undated"),
                Untagged = q.Bool("untagged"),
            };

            var sort = q.Enum("sort", TaskQuery.Sorts, TaskQuery.DefaultSort);
            var limit = q.Int("limit", min: 1, max: 200, fallback: 50);
            var cursor = QueryStrings.Optional(ctx, q, "cursor", maxLength: 64);

            q.ThrowIfInvalid("invalid_query", "Invalid task list query.");

            var page = await TaskQuery
                .ListAsync(repository.Tasks, user.Id, filter, sort, limit, cursor, cancellationToken: ct)
                .ConfigureAwait(false);

            // Overlaid on the way OUT, and nowhere earlier: the filter and sort ran
            // against the canonical text, so the rows the user asked for are the
            // rows they get — only the words change.
            var locale = await locales.ReadAsync(user.Id, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                tasks = MatterLocale.PresentMany(page.Tasks, locale),
                total = page.Total,
                nextCursor = page.NextCursor,
            });
        }).RequireAuthorization();

        // `tz` is the caller's IANA zone — day boundaries are meaningless without
        // it, and an UNRECOGNISED one is a 500 rather than a silent UTC fallback.
        endpoints.MapGet("/me/tasks/counts", async (
            HttpContext ctx,
            TaskCountsService counts,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            var q = new QueryReader(ctx.Request.Query, "tz");
            var tz = q.String("tz", maxLength: 64);
            q.ThrowIfInvalid("invalid_query", "Invalid counts query.");

            return Results.Ok(new { counts = await counts.ComputeAsync(user.Id, tz, cancellationToken: ct).ConfigureAwait(false) });
        }).RequireAuthorization();

        // Distinct tag list for autocomplete. Cheap: it rides the {userId, tags}
        // index.
        endpoints.MapGet("/me/tasks/tags", async (
            HttpContext ctx,
            TaskRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            return Results.Ok(new { tags = await repository.DistinctTagsAsync(user.Id, ct).ConfigureAwait(false) });
        }).RequireAuthorization();
    }
}

/// <summary>
/// Two query-string readings the kernel's <c>QueryReader</c> does not cover.
/// </summary>
internal static class QueryStrings
{
    /// <summary>
    /// A <c>z.string().max(n)</c> with no minimum: an EMPTY value is legal and
    /// means "absent" downstream. <c>QueryReader.Raw</c> records an issue for an
    /// empty value, which is right for the <c>min(1)</c> fields and wrong for these.
    /// </summary>
    public static string? Optional(HttpContext ctx, QueryReader reader, string key, int maxLength)
    {
        if (!ctx.Request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        if (value.Length > maxLength)
        {
            reader.AddIssue(key, ZodMessages.TooLong(maxLength));
            return null;
        }

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// zod's <c>.datetime()</c>, which requires a full ISO-8601 INSTANT.
    ///
    /// <para>
    /// <c>QueryReader.IsoDate</c> delegates to <c>DateTime.TryParse</c>, which
    /// happily accepts a bare <c>2026-09-01</c> — zod does not, and the contract
    /// pins the rejection (<c>list-bad-datetime</c>). So the parse is done here
    /// with the stricter rule and the issue is pushed back onto the shared reader,
    /// keeping the accumulate-then-throw-once contract intact.
    /// </para>
    /// </summary>
    public static DateTime? IsoInstant(HttpContext ctx, QueryReader reader, string key)
    {
        if (!ctx.Request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var raw = values.ToString();
        if (raw.Length == 0)
        {
            reader.AddIssue(key, ZodMessages.TooShort(1));
            return null;
        }

        if (!BodyFields.TryParseIsoInstant(raw, out var parsed))
        {
            reader.AddIssue(key, ZodMessages.InvalidDatetime);
            return null;
        }

        return parsed;
    }
}
