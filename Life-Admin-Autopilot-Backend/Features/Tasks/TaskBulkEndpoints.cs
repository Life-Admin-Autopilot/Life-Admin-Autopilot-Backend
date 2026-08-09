using System.Text.Json;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Tasks.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// zod strings this slice needs that the kernel's <c>ZodMessages</c> does not
/// carry. Kept here rather than added to the kernel, which is frozen.
/// </summary>
internal static class TaskZodMessages
{
    /// <summary>
    /// <c>z.discriminatedUnion</c>'s rejection. Note it names the options but does
    /// NOT echo what was received — unlike <c>z.enum</c>. Verified live.
    /// </summary>
    public static string InvalidDiscriminator(IEnumerable<string> options) =>
        $"Invalid discriminator value. Expected {string.Join(" | ", options.Select(o => $"'{o}'"))}";

    public const string ProvideExactlyOneTarget = "provide exactly one of ids or filter";
}

/// <summary>
/// Bulk preview / apply, and undo.
///
/// <para>
/// <b>These bodies are LENIENT.</b> <c>BulkTargetSchema</c> and
/// <c>BulkActionSchema</c> carry no <c>.strict()</c>, so an unknown key is
/// stripped and the request succeeds — verified live. That is a real divergence
/// from the rest of <c>me.tasks</c>, and from the blanket statement in
/// KERNEL.md §4.
/// </para>
/// </summary>
internal static class TaskBulkEndpoints
{
    private static readonly string[] Actions = { "delete", "complete", "snooze", "setDomain", "addTags" };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Dry run. The confirm card needs the exact count and the ripple warnings
        // BEFORE anything happens — a natural-language range must never be deleted
        // from directly, only from a resolved, previewed set.
        endpoints.MapPost("/me/tasks/bulk/preview", async (
            HttpContext ctx,
            BulkService bulk,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var parsed = await ReadBulkBodyAsync(ctx, ct).ConfigureAwait(false);
            var target = parsed.Target;

            var tasks = await bulk
                .ResolveTargetsAsync(user.Id, target, DateTime.UtcNow, ct)
                .ConfigureAwait(false);

            var warnings = BulkService.SummarizeWarnings(tasks);

            return Results.Ok(new
            {
                count = tasks.Count,
                warnings = new
                {
                    fromDocuments = warnings.FromDocuments,
                    remindersFired = warnings.RemindersFired,
                    truncated = warnings.Truncated,
                },

                // Enough to scroll through and sanity-check what is about to change.
                sample = tasks.Take(50).Select(t => t.ToDto()).ToList(),
            });
        }).RequireAuthorization();

        endpoints.MapPost("/me/tasks/bulk", async (
            HttpContext ctx,
            BulkService bulk,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var parsed = await ReadBulkBodyAsync(ctx, ct).ConfigureAwait(false);

            var result = await bulk
                .ApplyAsync(user.Id, parsed.Target, parsed.Action!, parsed.Label, cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                affected = result.Affected,

                // Null rather than absent when nothing changed: a no-op op is never
                // journaled, so there is nothing to undo.
                undoToken = result.UndoToken,
                warnings = new
                {
                    fromDocuments = result.Warnings.FromDocuments,
                    remindersFired = result.Warnings.RemindersFired,
                    truncated = result.Warnings.Truncated,
                },
            });
        }).RequireAuthorization();

        endpoints.MapPost("/me/tasks/undo/{token}", async (
            string token,
            HttpContext ctx,
            BulkService bulk,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            // Two DIFFERENT literal messages behind one code, both pinned by the
            // contract — a malformed token says one thing, a well-formed but
            // unknown one names the 30-day window. BulkService owns both strings.
            var restored = await bulk.UndoAsync(user.Id, token, ct).ConfigureAwait(false);

            return Results.Ok(new { restored });
        }).RequireAuthorization();
    }

    private readonly record struct ParsedBulkBody(BulkTarget Target, BulkActionInput? Action, string? Label);

    /// <summary>
    /// <c>z.intersection(BulkTargetSchema, BulkActionSchema)</c>: BOTH halves are
    /// parsed and their issues merged into one response.
    /// </summary>
    private static async Task<ParsedBulkBody> ReadBulkBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        var body = await KernelBody
            .ReadAsync<BulkBody>(ctx, KernelBodyOptions.Lenient("Invalid bulk request."), ct)
            .ConfigureAwait(false);

        // Read straight off the body and truncated by the route — deliberately not
        // part of either zod half, which would have stripped it.
        var label = body.Label.ValueKind == JsonValueKind.String
            ? Truncate(body.Label.GetString(), 240)
            : null;

        var f = new BodyFields();
        var target = ReadTarget(f, body.Ids, body.Filter);
        var action = ReadAction(f, body.Action, body.Until, body.Domain, body.Tags);

        f.ThrowIfInvalid("invalid_body", "Invalid bulk request.");
        return new ParsedBulkBody(target, action, label);
    }

    /// <summary>
    /// Either an explicit id list or a filter — never both, never neither. The xor
    /// is a schema-level refine, so it reports as a FORM error.
    /// </summary>
    public static BulkTarget ReadTarget(BodyFields f, JsonElement ids, JsonElement filter)
    {
        var hasIds = BodyFields.HasValue(ids);
        var hasFilter = BodyFields.HasValue(filter);

        List<string>? idList = null;
        if (hasIds)
        {
            idList = f.StringArray(ids, "ids", itemMax: 64, arrayMax: BulkService.MaxBulkTargets, arrayMin: 1);
            if (idList is not null)
            {
                foreach (var raw in idList.Where(raw => !ObjectId.TryParse(raw, out _)))
                {
                    f.AddIssue("ids", "invalid_object_id");
                    _ = raw;
                }
            }
        }

        if (hasIds == hasFilter)
        {
            f.AddFormIssue(TaskZodMessages.ProvideExactlyOneTarget);
            return new BulkTarget();
        }

        return new BulkTarget
        {
            Ids = hasIds ? idList : null,
            Filter = hasFilter ? ReadFilter(f, filter) : null,
        };
    }

    private static BulkActionInput? ReadAction(
        BodyFields f,
        JsonElement action,
        JsonElement until,
        JsonElement domain,
        JsonElement tags)
    {
        if (!BodyFields.HasValue(action) || action.ValueKind != JsonValueKind.String)
        {
            f.AddIssue("action", TaskZodMessages.InvalidDiscriminator(Actions));
            return null;
        }

        switch (action.GetString())
        {
            case "delete":
                return new BulkActionInput.Delete();

            case "complete":
                return new BulkActionInput.Complete();

            case "snooze":
                var moment = f.IsoDate(until, "until");
                if (BodyFields.IsAbsent(until))
                {
                    f.AddIssue("until", ZodMessages.Required);
                }

                return moment.HasValue ? new BulkActionInput.Snooze(moment.Value) : null;

            case "setDomain":
                var target = f.Enum(domain, "domain", TaskVocabulary.Domains, required: true);
                return target is null ? null : new BulkActionInput.SetDomain(target);

            case "addTags":
                var list = f.StringArray(tags, "tags", itemMax: 64, arrayMax: TaskVocabulary.MaxTags, arrayMin: 1);
                if (BodyFields.IsAbsent(tags))
                {
                    f.AddIssue("tags", ZodMessages.Required);
                }

                return list is null ? null : new BulkActionInput.AddTags(list);

            default:
                f.AddIssue("action", TaskZodMessages.InvalidDiscriminator(Actions));
                return null;
        }
    }

    /// <summary>
    /// <c>TaskFilterSchema</c> nested inside a body. The multi-value members are
    /// still COMMA-SEPARATED STRINGS here, not arrays — the same <c>csvEnum</c> the
    /// query string uses, so the agent and the REST list cannot drift apart.
    /// </summary>
    public static TaskQuery.TaskFilter ReadFilter(BodyFields f, JsonElement filter)
    {
        if (filter.ValueKind != JsonValueKind.Object)
        {
            f.AddIssue("filter", ZodMessages.ExpectedType("object", filter.ValueKind.ToString().ToLowerInvariant()));
            return new TaskQuery.TaskFilter();
        }

        JsonElement Member(string name) => filter.TryGetProperty(name, out var value) ? value : default;

        return new TaskQuery.TaskFilter
        {
            Q = f.TrimmedString(Member("q"), "filter", min: 1, max: 200, required: false),
            Status = Csv(f, Member("status"), TaskVocabulary.Statuses),
            Domain = Csv(f, Member("domain"), TaskVocabulary.Domains),
            Priority = Csv(f, Member("priority"), TaskVocabulary.Priorities),
            Kind = Csv(f, Member("kind"), TaskVocabulary.Kinds),
            Tag = f.PlainString(Member("tag"), "filter", max: 400),
            DueBefore = f.IsoDate(Member("dueBefore"), "filter"),
            DueAfter = f.IsoDate(Member("dueAfter"), "filter"),
            CreatedBefore = f.IsoDate(Member("createdBefore"), "filter"),
            CreatedAfter = f.IsoDate(Member("createdAfter"), "filter"),
            CompletedBefore = f.IsoDate(Member("completedBefore"), "filter"),
            CompletedAfter = f.IsoDate(Member("completedAfter"), "filter"),
            Overdue = f.Bool(Member("overdue"), "filter"),
            Undated = f.Bool(Member("undated"), "filter"),
            Untagged = f.Bool(Member("untagged"), "filter"),
        };
    }

    private static IReadOnlyList<string>? Csv(BodyFields f, JsonElement element, IReadOnlyList<string> allowed)
    {
        if (!BodyFields.HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            f.AddIssue("filter", ZodMessages.ExpectedType("string", element.ValueKind.ToString().ToLowerInvariant()));
            return null;
        }

        var parts = (element.GetString() ?? string.Empty)
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var invalid = false;
        foreach (var part in parts.Where(p => !allowed.Contains(p, StringComparer.Ordinal)))
        {
            f.AddIssue("filter", ZodMessages.CsvEnumMember(allowed, part));
            invalid = true;
        }

        if (parts.Count == 0)
        {
            f.AddIssue("filter", ZodMessages.MustNotBeEmpty);
            return null;
        }

        return invalid ? null : parts;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
