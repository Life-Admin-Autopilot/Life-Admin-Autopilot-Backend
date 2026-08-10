using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// The three AI routes that work with <b>no model at all</b> — the conversation
/// read, the reset, and the quota meter. Ported from
/// <c>server/src/modules/ai/routes.ts</c>.
///
/// <para>
/// <b>None of them is rate limited and none consults <c>isAiConfigured()</c>.</b>
/// That is deliberate upstream: the chat surface must still render its history and
/// its remaining-messages counter on a deployment with no key, which is exactly the
/// state the parity reference runs in.
/// </para>
/// </summary>
public static class AiConversationEndpoints
{
    public static IEndpointRouteBuilder MapAiConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /ai/conversation — upserts an empty document on first read, so it
        // never 404s. Scope is hard-coded; there are no query parameters, and an
        // unknown one is ignored rather than rejected.
        endpoints.MapGet("/ai/conversation", async (
            HttpContext context,
            AiConversationService conversations,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            return Results.Ok(await conversations.LoadAsync(caller.Id, cancellationToken).ConfigureAwait(false));
        })
        .RequireAuthorization();

        // POST /ai/conversation/reset — no request body is read at all, so a
        // malformed or oversize one cannot fail it. Always answers the literal
        // empty envelope.
        endpoints.MapPost("/ai/conversation/reset", async (
            HttpContext context,
            AiConversationService conversations,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            return Results.Ok(await conversations.ResetAsync(caller.Id, cancellationToken).ConfigureAwait(false));
        })
        .RequireAuthorization();

        // GET /ai/quota — one meter per member of AI_USAGE_KINDS, which has exactly
        // one. `tier` comes from resolveTier(), a v1 constant.
        endpoints.MapGet("/ai/quota", async (
            HttpContext context,
            AiQuotaService quota,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var tier = AiQuotaService.ResolveTier();

            return Results.Ok(new AiQuotaResponse
            {
                Tier = tier,
                Quotas = await quota
                    .GetStatusAsync(caller.Id, tier, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
            });
        })
        .RequireAuthorization();

        return endpoints;
    }
}
