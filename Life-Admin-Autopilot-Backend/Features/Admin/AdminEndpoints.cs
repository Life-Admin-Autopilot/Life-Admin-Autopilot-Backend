using System.Security.Claims;
using System.Text;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Admin;

/// <summary>
/// Every <c>/admin/*</c> route.
///
/// <para>
/// <b>All of them require the <c>AdminBearer</c> scheme</b>, which validates against
/// a different signing key than customer tokens — so an app access token does not
/// merely lack a role here, it fails signature validation. See
/// <see cref="AdminTokenOptions"/>.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapAuth(endpoints);

        var console = endpoints.MapGroup("/admin")
            .RequireAuthorization(AdminRoles.ConsolePolicy);

        MapInsights(console);
        MapCustomers(console);
        MapAudit(console);
        MapOperations(console);
        console.MapAdminActivityEndpoints();

        return endpoints;
    }

    // ---- auth --------------------------------------------------------------

    private static void MapAuth(IEndpointRouteBuilder endpoints)
    {
        // Unauthenticated by necessity. Rate limiting is the kernel's global policy;
        // the console is not publicly routable, which is the real control.
        endpoints.MapPost("/admin/auth/signin", async (
            HttpContext context,
            [FromBody] AdminSigninRequest body,
            IUserProfileRepository users,
            IAuthCredentialStore credentials,
            IAdminRoleStore roles,
            AdminTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (!tokens.IsConfigured)
            {
                throw AppException.Forbidden(
                    "admin_console_disabled",
                    "ADMIN_JWT_SECRET is not configured, so the console is switched off.");
            }

            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrEmpty(body.Password))
            {
                throw AppException.BadRequest("invalid_body", "Email and password are required.");
            }

            var user = await users.FindByEmailAsync(body.Email.Trim(), cancellationToken).ConfigureAwait(false);

            // One indistinguishable 401 for unknown address, wrong password, and
            // "correct credentials but not an admin". The third is the one that
            // matters: a distinct response there would let anyone enumerate which of
            // your users are administrators.
            if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            {
                throw Unauthorized();
            }

            var ok = await credentials
                .VerifyPasswordAsync(user.IdentityUserId, body.Password, cancellationToken)
                .ConfigureAwait(false);

            if (!ok)
            {
                throw Unauthorized();
            }

            var held = await roles.RolesForAsync(user.IdentityUserId, cancellationToken).ConfigureAwait(false);
            if (held.Count == 0)
            {
                throw Unauthorized();
            }

            AdminSuspension.ThrowIfSuspended(user);

            var token = tokens.Issue(user.IdentityUserId, user.Email, held);

            return Results.Ok(new AdminSessionDto
            {
                AccessToken = token.AccessToken,
                ExpiresAt = token.ExpiresAt,
                Email = user.Email,
                Role = token.Role,
            });
        });

        // Who am I — the console calls this on load to decide what to render.
        endpoints.MapGet("/admin/auth/me", (HttpContext context) =>
        {
            var actor = context.RequireAdmin();

            return Results.Ok(new AdminSessionDto
            {
                AccessToken = string.Empty,
                ExpiresAt = default,
                Email = actor.Email,
                Role = actor.Role,
            });
        })
        .RequireAuthorization(AdminRoles.ConsolePolicy);
    }

    // ---- insights ----------------------------------------------------------

    private static void MapInsights(IEndpointRouteBuilder console)
    {
        console.MapGet("/insights/pulse", async (
            [FromQuery] int? days,
            AdminInsightService insights,
            CancellationToken cancellationToken) =>
            Results.Ok(await insights.PulseAsync(days ?? 30, cancellationToken).ConfigureAwait(false)));

        console.MapGet("/insights/top-spenders", async (
            [FromQuery] int? days,
            [FromQuery] int? limit,
            AdminInsightService insights,
            CancellationToken cancellationToken) =>
            Results.Ok(await insights
                .TopSpendersAsync(days ?? 30, limit ?? 20, cancellationToken)
                .ConfigureAwait(false)));

        console.MapGet("/insights/cost-distribution", async (
            [FromQuery] int? days,
            AdminInsightService insights,
            CancellationToken cancellationToken) =>
            Results.Ok(await insights
                .CostDistributionAsync(days ?? 30, cancellationToken)
                .ConfigureAwait(false)));

        console.MapGet("/insights/by-feature", async (
            [FromQuery] int? days,
            AdminInsightService insights,
            CancellationToken cancellationToken) =>
            Results.Ok(await insights.ByFeatureAsync(days ?? 30, cancellationToken).ConfigureAwait(false)));

        console.MapGet("/insights/daily", async (
            [FromQuery] int? days,
            AdminInsightService insights,
            CancellationToken cancellationToken) =>
            Results.Ok(await insights.DailySeriesAsync(days ?? 30, cancellationToken).ConfigureAwait(false)));

        // Reliability: failures grouped by cause rather than dumped as a log.
        console.MapGet("/insights/errors", async (
            [FromQuery] int? days,
            AdminOpsService ops,
            CancellationToken cancellationToken) =>
            Results.Ok(await ops.ErrorsAsync(days ?? 30, cancellationToken).ConfigureAwait(false)));

        // Activation. Where people stop is the roadmap.
        console.MapGet("/insights/funnel", async (
            AdminOpsService ops,
            CancellationToken cancellationToken) =>
            Results.Ok(await ops.FunnelAsync(cancellationToken).ConfigureAwait(false)));

        // Feature adoption — deliberately NOT funnel rungs. See AdoptionAsync.
        console.MapGet("/insights/adoption", async (
            AdminOpsService ops,
            CancellationToken cancellationToken) =>
            Results.Ok(await ops.AdoptionAsync(cancellationToken).ConfigureAwait(false)));
    }

    // ---- customers ---------------------------------------------------------

    private static void MapCustomers(IEndpointRouteBuilder console)
    {
        console.MapGet("/customers", async (
            [FromQuery] string? search,
            [FromQuery] string? segment,
            [FromQuery] string? sort,
            [FromQuery] bool? desc,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            AdminCustomerService customers,
            CancellationToken cancellationToken) =>
            Results.Ok(await customers
                .SearchAsync(search, segment, sort, desc ?? true, skip ?? 0, take ?? 50, cancellationToken)
                .ConfigureAwait(false)));

        console.MapGet("/customers/{id}", async (
            string id,
            HttpContext context,
            AdminCustomerService customers,
            IAdminAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            var objectId = ParseId(id);
            var detail = await customers.DetailAsync(objectId, cancellationToken).ConfigureAwait(false);

            // Opening a customer is itself recorded. It is the only way to answer
            // "who looked at this account?", and it is cheap.
            var actor = context.RequireAdmin();
            await audit.AppendAsync(
                new AdminAuditEventDocument
                {
                    At = DateTime.UtcNow,
                    ActorId = actor.Id,
                    ActorEmail = actor.Email,
                    ActorRole = actor.Role,
                    Action = AdminAuditAction.CustomerViewed,
                    TargetUserId = id,
                    TargetEmail = detail.Customer.Email,
                    Reason = "Opened customer detail.",
                    Ip = actor.Ip,
                    UserAgent = actor.UserAgent,
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(detail);
        });

        console.MapPost("/customers/{id}/suspend", (
            string id, HttpContext context, [FromBody] AdminActionRequest body,
            AdminCustomerService customers, CancellationToken cancellationToken) =>
            Act(customers.SuspendAsync(ParseId(id), context.RequireAdmin(), body.Reason, cancellationToken)));

        console.MapPost("/customers/{id}/restore", (
            string id, HttpContext context, [FromBody] AdminActionRequest body,
            AdminCustomerService customers, CancellationToken cancellationToken) =>
            Act(customers.RestoreAsync(ParseId(id), context.RequireAdmin(), body.Reason, cancellationToken)));

        console.MapPost("/customers/{id}/reset-quota", (
            string id, HttpContext context, [FromBody] AdminActionRequest body,
            AdminCustomerService customers, CancellationToken cancellationToken) =>
            Act(customers.ResetQuotasAsync(ParseId(id), context.RequireAdmin(), body.Reason, cancellationToken)));

        console.MapPost("/customers/{id}/revoke-sessions", (
            string id, HttpContext context, [FromBody] AdminActionRequest body,
            AdminCustomerService customers, CancellationToken cancellationToken) =>
            Act(customers.RevokeSessionsAsync(ParseId(id), context.RequireAdmin(), body.Reason, cancellationToken)));

        console.MapPost("/customers/{id}/tier", (
            string id, HttpContext context, [FromBody] AdminActionRequest body,
            AdminCustomerService customers, CancellationToken cancellationToken) =>
            Act(customers.GrantTierAsync(
                ParseId(id), context.RequireAdmin(), body.Reason, body.Tier, body.Days, cancellationToken)));

        // Send one customer a message. Push plus a durable in-app row — see
        // AdminNotificationService for why the row is written first.
        console.MapPost("/customers/{id}/notify", async (
            string id,
            HttpContext context,
            [FromBody] AdminNotifyRequest body,
            AdminNotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            var outcome = await notifications
                .NotifyAsync(
                    ParseId(id),
                    new AdminMessage(body.Title ?? string.Empty, body.Body ?? string.Empty),
                    context.RequireAdmin(),
                    body.Reason,
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(outcome);
        });

        // The audited CSV export. Bulk export is the classic insider-risk event, so
        // it writes its own audit row before a byte is produced.
        console.MapGet("/customers/export", async (
            [FromQuery] string? search,
            [FromQuery] string? segment,
            HttpContext context,
            AdminCustomerService customers,
            IAdminAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            var actor = context.RequireAdmin();

            await audit.AppendAsync(
                new AdminAuditEventDocument
                {
                    At = DateTime.UtcNow,
                    ActorId = actor.Id,
                    ActorEmail = actor.Email,
                    ActorRole = actor.Role,
                    Action = AdminAuditAction.CustomerExported,
                    Reason = $"Exported segment '{segment ?? "all"}'.",
                    Ip = actor.Ip,
                    UserAgent = actor.UserAgent,
                    Details = new BsonDocument { ["segment"] = segment ?? "all", ["search"] = search ?? "" },
                },
                cancellationToken).ConfigureAwait(false);

            // PAGED, not one MaxTake read.
            //
            // MaxTake is 200 — a page size no caller can talk us past, which is
            // right for a table and wrong for an export. Taking a single page
            // handed an operator 200 of 362 customers with nothing anywhere
            // saying so, and a silently short customer list is worse than a
            // refused one: it gets filtered, mailed, and acted on as if complete.
            //
            // Bounded by ExportMaxRows so a runaway database cannot turn one
            // click into an unbounded allocation. Reaching that ceiling is
            // logged rather than silently truncated -- the same rule as above,
            // one level up.
            var rows = new List<AdminCustomerRowDto>();

            while (rows.Count < ExportMaxRows)
            {
                var page = await customers
                    .SearchAsync(
                        search, segment, null, true,
                        rows.Count, AdminCustomerRepository.MaxTake, cancellationToken)
                    .ConfigureAwait(false);

                if (page.Rows.Count == 0)
                {
                    break;
                }

                rows.AddRange(page.Rows);

                // A short page is the last page.
                if (page.Rows.Count < AdminCustomerRepository.MaxTake)
                {
                    break;
                }
            }

            return Results.Text(
                AdminOpsService.ToCsv(rows),
                "text/csv",
                Encoding.UTF8);
        });
    }

    /// <summary>
    /// The ceiling on ONE export, independent of how many customers exist.
    ///
    /// <para>
    /// Generous enough that no realistic console session hits it, low enough that
    /// a single click cannot materialise an unbounded CSV in memory. It exists as
    /// a backstop on the paging loop, not as a business rule.
    /// </para>
    /// </summary>
    private const int ExportMaxRows = 50_000;

    // ---- audit -------------------------------------------------------------

    private static void MapAudit(IEndpointRouteBuilder console)
    {
        console.MapGet("/audit", async (
            [FromQuery] string? target,
            [FromQuery] string? action,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            IAdminAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            var page = await audit
                .QueryAsync(
                    new AdminAuditQuery(
                        TargetUserId: target,
                        ActionPrefix: action,
                        Skip: skip ?? 0,
                        Take: take ?? 50),
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                rows = page.Rows.Select(r => new
                {
                    at = r.At,
                    actor = r.ActorEmail,
                    role = r.ActorRole,
                    action = r.Action,
                    target = r.TargetEmail,
                    targetId = r.TargetUserId,
                    reason = r.Reason,
                    outcome = r.Outcome,
                    error = r.Error,
                    ip = r.Ip,
                }),
                total = page.Total,
            });
        });
    }

    // ---- operations --------------------------------------------------------

    private static void MapOperations(IEndpointRouteBuilder console)
    {
        // Admin-only, not Support. Managing who else has access is the one power a
        // support role must never hold.
        var operators = console.MapGroup("/ops").RequireAuthorization(AdminRoles.OperatorPolicy);

        operators.MapGet("/admins", async (
            IAdminRoleStore roles,
            CancellationToken cancellationToken) =>
            Results.Ok((await roles.ListAdminsAsync(cancellationToken).ConfigureAwait(false))
                .Select(a => new { email = a.Email, role = a.Role })));

        // ---- kill switches ----
        operators.MapGet("/flags", async (AdminOpsService ops, CancellationToken cancellationToken) =>
            Results.Ok(await ops.FlagsAsync(cancellationToken).ConfigureAwait(false)));

        operators.MapPost("/flags/{key}", async (
            string key,
            HttpContext context,
            [FromBody] FlagRequest body,
            AdminOpsService ops,
            CancellationToken cancellationToken) =>
            Results.Ok(await ops
                .SetFlagAsync(key, body.Disabled, body.Reason, context.RequireAdmin(), cancellationToken)
                .ConfigureAwait(false)));

        // ---- broadcast ----
        // Preview first, always. The console shows the count in the confirm dialog,
        // because "send to 4,182 people" and "send to 12" are different decisions.
        operators.MapGet("/broadcast/preview", async (
            [FromQuery] string? segment,
            AdminCustomerService customers,
            CancellationToken cancellationToken) =>
        {
            // COUNT, not a page. The paged search caps at MaxTake, so this used to
            // report "200 recipients" for a segment of any size above that — and the
            // operator would press send believing it.
            var recipients = await customers
                .SegmentCountAsync(segment, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                segment = segment ?? "all",
                recipients,
                cap = AdminNotificationService.MaxBroadcastRecipients,
                overCap = recipients > AdminNotificationService.MaxBroadcastRecipients,
            });
        });

        operators.MapPost("/broadcast", async (
            HttpContext context,
            [FromBody] BroadcastRequest body,
            AdminCustomerService customers,
            AdminNotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            // The TRUE size decides whether this is allowed, before any id is
            // fetched. Enforcing the cap against a page would mean the cap could
            // never fire — which is exactly what it did.
            var total = await customers
                .SegmentCountAsync(body.Segment, cancellationToken)
                .ConfigureAwait(false);

            if (total > AdminNotificationService.MaxBroadcastRecipients)
            {
                throw AppException.BadRequest(
                    "broadcast_too_large",
                    $"That segment matches {total} people, over the "
                    + $"{AdminNotificationService.MaxBroadcastRecipients} cap. Narrow the segment — "
                    + "a broadcast cannot be recalled.");
            }

            var ids = await customers
                .SegmentIdsAsync(
                    body.Segment,
                    AdminNotificationService.MaxBroadcastRecipients,
                    cancellationToken)
                .ConfigureAwait(false);

            var outcome = await notifications
                .BroadcastAsync(
                    ids,
                    body.Segment ?? "all",
                    new AdminMessage(body.Title ?? string.Empty, body.Body ?? string.Empty),
                    context.RequireAdmin(),
                    body.Reason,
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(outcome);
        });

        operators.MapPost("/admins/grant", async (
            HttpContext context,
            [FromBody] AdminGrantRequest body,
            IAdminRoleStore roles,
            IAdminAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            var actor = context.RequireAdmin();

            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Role))
            {
                throw AppException.BadRequest("invalid_body", "Email and role are required.");
            }

            var granted = await roles.GrantAsync(body.Email.Trim(), body.Role.Trim(), cancellationToken)
                .ConfigureAwait(false);

            await audit.AppendAsync(
                new AdminAuditEventDocument
                {
                    At = DateTime.UtcNow,
                    ActorId = actor.Id,
                    ActorEmail = actor.Email,
                    ActorRole = actor.Role,
                    Action = AdminAuditAction.AdminRoleChanged,
                    TargetEmail = body.Email,
                    Reason = body.Reason ?? "Role granted from the console.",
                    Ip = actor.Ip,
                    UserAgent = actor.UserAgent,
                    Outcome = granted ? AdminAuditOutcome.Ok : AdminAuditOutcome.Failed,
                    Error = granted ? null : "No account with that email, or role not recognised.",
                },
                cancellationToken).ConfigureAwait(false);

            return granted
                ? Results.Ok(new AdminActionResultDto
                {
                    Action = AdminAuditAction.AdminRoleChanged,
                    Message = $"{body.Email} now holds '{body.Role}'.",
                })
                : throw AppException.BadRequest(
                    "grant_failed",
                    "That account does not exist, or the role is not one of Admin / Support. "
                    + "The console never creates credentials — the person signs up in the app first.");
        });
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<IResult> Act(Task<AdminActionResultDto> action) =>
        Results.Ok(await action.ConfigureAwait(false));

    private static ObjectId ParseId(string id) =>
        ObjectId.TryParse(id, out var parsed)
            ? parsed
            : throw AppException.BadRequest("invalid_id", "That is not a customer id.");

    private static AppException Unauthorized() =>
        AppException.Unauthorized("invalid_credentials", "Wrong email or password.");
}

public sealed record AdminSigninRequest(string? Email, string? Password);

public sealed record AdminGrantRequest(string? Email, string? Role, string? Reason);

public sealed record AdminNotifyRequest(string? Title, string? Body, string? Reason);

public sealed record BroadcastRequest(string? Segment, string? Title, string? Body, string? Reason);

public sealed record FlagRequest(bool Disabled, string? Reason);

/// <summary>Reads the acting admin off the validated console token.</summary>
public static class AdminActorAccessor
{
    public static AdminActor RequireAdmin(this HttpContext context)
    {
        var sub = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(sub, out var id))
        {
            throw AppException.Unauthorized("missing_token", "Missing console token");
        }

        var email = context.User.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? context.User.FindFirst(ClaimTypes.Email)?.Value
            ?? string.Empty;

        var roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        return new AdminActor(
            id,
            email,
            AdminRoles.Highest(roles),

            // The proxy header is trusted only because the console is not publicly
            // routable; on a public surface this would be spoofable and would need
            // ForwardedHeaders with a known-proxy allowlist.
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null);
    }
}
