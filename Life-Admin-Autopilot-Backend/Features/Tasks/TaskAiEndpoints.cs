using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Tasks.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// The AI-backed Matters routes, plus the two review/metering endpoints that
/// work without a model.
///
/// <para>
/// <b>The gate order is inconsistent, observable, and pinned.</b> Four routes
/// validate the body BEFORE checking whether AI is configured, so a malformed
/// request reports what is actually wrong with it. <c>/summarize</c> checks
/// AI-configured FIRST, so a malformed body there returns <b>503, not 400</b>.
/// That asymmetry is reproduced deliberately — a port that tidied it up would
/// break the client's error handling on exactly one route.
/// </para>
///
/// <para><b>Five distinct 503 messages</b>, one per route. See
/// <see cref="AiUnavailable"/>.</para>
/// </summary>
internal static class TaskAiEndpoints
{
    private const int MaxBacklogBatch = 40;
    private const int MaxCategorizeTargets = 80;
    private const int MaxTranslateTargets = 150;

    private static readonly string[] TranslatePresets = { "thisWeek", "thisMonth", "all" };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        MapSearch(endpoints);
        MapSummarize(endpoints);
        MapEstimateBacklog(endpoints);
        MapCategorize(endpoints);
        MapTranslate(endpoints);
    }

    // ---- Search -----------------------------------------------------------

    private static void MapSearch(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/me/tasks/search", async (
            HttpContext ctx,
            AiAvailability ai,
            CancellationToken ct) =>
        {
            ctx.RequireUser();

            var body = await KernelBody
                .ReadAsync<SearchBody>(ctx, KernelBodyOptions.Strict_("Invalid search request."), ct)
                .ConfigureAwait(false);

            // Validate BEFORE checking availability, so a malformed request always
            // reports what is actually wrong with it rather than masking as a 503.
            var f = new BodyFields();
            f.TrimmedString(body.Query, "query", min: 1, max: 400, required: true);
            f.PlainString(body.Timezone, "timezone", max: 64);
            f.ThrowIfInvalid("invalid_body", "Invalid search request.");

            if (!ai.IsConfigured)
            {
                throw AiUnavailable.Search();
            }

            throw AiUnavailable.NotWiredHere("POST /me/tasks/search");
        })
        .RequireAuthorization()
        .RateLimited(KernelRateLimiters.TaskSearch);

    // ---- Summarize --------------------------------------------------------

    private static void MapSummarize(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/me/tasks/summarize", async (
            HttpContext ctx,
            AiAvailability ai,
            CancellationToken ct) =>
        {
            ctx.RequireUser();

            // THE ODD ONE OUT: the availability guard runs before the body is even
            // read, so an invalid range still answers 503.
            if (!ai.IsConfigured)
            {
                throw AiUnavailable.Summaries();
            }

            var body = await KernelBody
                .ReadAsync<SummarizeBody>(ctx, KernelBodyOptions.Strict_("Invalid summary range."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            var from = f.IsoDate(body.From, "from");
            var to = f.IsoDate(body.To, "to");
            if (BodyFields.IsAbsent(body.From))
            {
                f.AddIssue("from", ZodMessages.Required);
            }

            if (BodyFields.IsAbsent(body.To))
            {
                f.AddIssue("to", ZodMessages.Required);
            }

            f.PlainString(body.Timezone, "timezone", max: 64);

            if (from.HasValue && to.HasValue && to.Value <= from.Value)
            {
                f.AddFormIssue("to must be after from");
            }

            f.ThrowIfInvalid("invalid_body", "Invalid summary range.");

            throw AiUnavailable.NotWiredHere("POST /me/tasks/summarize");
        })
        .RequireAuthorization()
        .SummaryLimited();

    // ---- Estimate backlog -------------------------------------------------

    private static void MapEstimateBacklog(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/me/tasks/estimate-backlog", async (
            HttpContext ctx,
            AiAvailability ai,
            CancellationToken ct) =>
        {
            ctx.RequireUser();

            var body = await KernelBody
                .ReadAsync<EstimateBacklogBody>(ctx, KernelBodyOptions.Strict_("Invalid backfill request."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            f.Int(body.Limit, "limit", min: 1, max: MaxBacklogBatch, required: false);
            f.ThrowIfInvalid("invalid_body", "Invalid backfill request.");

            if (!ai.IsConfigured)
            {
                throw AiUnavailable.Estimates();
            }

            throw AiUnavailable.NotWiredHere("POST /me/tasks/estimate-backlog");
        })
        .RequireAuthorization()
        .SummaryLimited();

    // ---- Categorize -------------------------------------------------------

    private static void MapCategorize(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/me/tasks/categorize", async (
            HttpContext ctx,
            AiAvailability ai,
            CancellationToken ct) =>
        {
            ctx.RequireUser();

            var body = await KernelBody
                .ReadAsync<BulkTargetBody>(ctx, KernelBodyOptions.Lenient("Invalid categorize request."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            TaskBulkEndpoints.ReadTarget(f, body.Ids, body.Filter);
            f.ThrowIfInvalid("invalid_body", "Invalid categorize request.");

            if (!ai.IsConfigured)
            {
                throw AiUnavailable.Categorising();
            }

            throw AiUnavailable.NotWiredHere("POST /me/tasks/categorize");
        })
        .RequireAuthorization()
        .SummaryLimited();

        endpoints.MapGet("/me/tasks/categorize/pending", async (
            HttpContext ctx,
            CategorizeService categorize,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            // Explicit null, not an omitted key — the client branches on it.
            var proposal = await categorize.GetPendingAsync(user.Id, ct).ConfigureAwait(false);
            return Results.Ok(new CategorizePendingResponse { Proposal = proposal });
        }).RequireAuthorization();

        endpoints.MapPost("/me/tasks/categorize/{id}/apply", async (
            string id,
            HttpContext ctx,
            CategorizeService categorize,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            var body = await KernelBody
                .ReadAsync<ApplyProposalBody>(ctx, KernelBodyOptions.Strict_("Invalid apply request."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            var taskIds = f.StringArray(body.TaskIds, "taskIds", itemMax: 64, arrayMax: MaxCategorizeTargets)
                          ?? new List<string>();

            if (BodyFields.IsAbsent(body.TaskIds))
            {
                f.AddIssue("taskIds", ZodMessages.Required);
            }

            foreach (var raw in taskIds.Where(raw => !ObjectId.TryParse(raw, out _)))
            {
                f.AddIssue("taskIds", "invalid_object_id");
                _ = raw;
            }

            f.ThrowIfInvalid("invalid_body", "Invalid apply request.");

            return Results.Ok(await categorize.ApplyAsync(user.Id, id, taskIds, cancellationToken: ct).ConfigureAwait(false));
        }).RequireAuthorization();

        endpoints.MapPost("/me/tasks/categorize/{id}/discard", async (
            string id,
            HttpContext ctx,
            CategorizeService categorize,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            await categorize.DiscardAsync(user.Id, id, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    // ---- Translate --------------------------------------------------------

    private static void MapTranslate(IEndpointRouteBuilder endpoints)
    {
        // No model call, so this one really answers. The sheet needs to show what a
        // selection would cost before the user spends the month on it.
        endpoints.MapGet("/me/tasks/translate/quota", async (
            HttpContext ctx,
            TranslateQuotaService quota,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            // resolveAiLocale is LENIENT — an unknown or absent tag falls back to
            // English rather than erroring, so there is no query validation here.
            var locale = AiLocales.Resolve(ctx.Request.Query["locale"].ToString());

            var status = quota.ReadAsync(user.Id, locale, cancellationToken: ct);
            var thisWeek = quota.CountTranslatableAsync(user.Id, locale, "thisWeek", cancellationToken: ct);
            var thisMonth = quota.CountTranslatableAsync(user.Id, locale, "thisMonth", cancellationToken: ct);
            var all = quota.CountTranslatableAsync(user.Id, locale, "all", cancellationToken: ct);

            await Task.WhenAll(status, thisWeek, thisMonth, all).ConfigureAwait(false);

            return Results.Ok(new
            {
                quota = await status.ConfigureAwait(false),
                counts = new
                {
                    thisWeek = await thisWeek.ConfigureAwait(false),
                    thisMonth = await thisMonth.ConfigureAwait(false),
                    all = await all.ConfigureAwait(false),
                },
                max = MaxTranslateTargets,
            });
        }).RequireAuthorization();

        endpoints.MapPost("/me/tasks/translate", async (
            HttpContext ctx,
            AiAvailability ai,
            CancellationToken ct) =>
        {
            ctx.RequireUser();

            var body = await KernelBody
                .ReadAsync<TranslateSelectionBody>(ctx, KernelBodyOptions.Lenient("Invalid translate request."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            f.Enum(body.Locale, "locale", AiLocales.All, required: true);
            f.StringArray(body.Ids, "ids", itemMax: 64, arrayMax: MaxTranslateTargets);
            f.Enum(body.Preset, "preset", TranslatePresets, required: false);
            f.ThrowIfInvalid("invalid_body", "Invalid translate request.");

            if (!ai.IsConfigured)
            {
                throw AiUnavailable.Translating();
            }

            throw AiUnavailable.NotWiredHere("POST /me/tasks/translate");
        })
        .RequireAuthorization()
        .SummaryLimited();
    }
}
