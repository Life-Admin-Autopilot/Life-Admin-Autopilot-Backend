using Life_Admin_Autopilot.BLL.Features.Digest;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;

namespace Life_Admin_Autopilot_Backend.Features.Digest;

/// <summary>
/// <c>GET /me/digest</c> — port of <c>server/src/routes/me.digest.ts</c>.
///
/// <para>
/// The dashboard's every-visit read, and the frontend's critical path. No rate
/// limiter. Deliberately NOT gated on AI availability.
/// </para>
/// </summary>
internal static class DigestEndpoints
{
    public static IEndpointRouteBuilder MapDigestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me/digest", async (
            HttpContext ctx,
            DailyDigestService digests,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            // `z.object({ tz: z.string().max(64).optional() }).strict()` — one of the
            // only two strict QUERY schemas in the API, so an unknown parameter is a
            // 400 rather than being ignored.
            var q = new QueryReader(ctx.Request.Query, "tz");

            // Read WITHOUT QueryReader.String: that helper enforces a min(1) and
            // TRIMS, and this field has neither. Both differences are observable —
            // `?tz=` is a valid empty string that simply means "no zone", and
            // `?tz=%20America/New_York%20` must stay untrimmed so it fails the zone
            // lookup and falls back to UTC exactly as Intl does on Node.
            var tz = DigestQuery.Timezone(ctx, q);

            q.ThrowIfInvalid("invalid_query", "Invalid digest query.");

            // An INVALID zone does not fail the request — buildDailyDigest validates
            // it and falls back to UTC, because a client typo must not take down the
            // one read the dashboard cannot render without.
            var result = await digests.BuildAsync(user.Id, tz, cancellationToken: ct).ConfigureAwait(false);

            return Results.Ok(new DigestResponse { Digest = result.ToDto() });
        }).RequireAuthorization();

        return endpoints;
    }
}

/// <summary>The one query field, bound to the exact zod schema behind it.</summary>
internal static class DigestQuery
{
    private const int MaxTimezoneLength = 64;

    /// <summary>
    /// <c>z.string().max(64).optional()</c>: no minimum, no trim. Only the LENGTH is
    /// checked here — the zone itself is validated downstream, where a bad one is a
    /// fallback rather than an error.
    /// </summary>
    public static string? Timezone(HttpContext ctx, QueryReader reader)
    {
        if (!ctx.Request.Query.TryGetValue("tz", out var values))
        {
            return null;
        }

        var value = values.ToString();
        if (value.Length > MaxTimezoneLength)
        {
            reader.AddIssue("tz", ZodMessages.TooLong(MaxTimezoneLength));
            return null;
        }

        // An empty string is legal and falsy, which `safeTimezone` reads as absent.
        return value.Length == 0 ? null : value;
    }
}
