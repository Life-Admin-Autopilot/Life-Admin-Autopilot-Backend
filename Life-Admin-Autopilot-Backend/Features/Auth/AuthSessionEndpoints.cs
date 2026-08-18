using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Auth;

/// <summary>Port of <c>server/src/routes/auth.session.ts</c>.</summary>
public static class AuthSessionEndpoints
{
    public static void MapAuthSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/refresh", RefreshAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.Auth);

        // The four below are authenticated but NOT rate limited, matching the
        // reference. Do not add a limiter for symmetry.
        endpoints.MapPost("/auth/signout", SignoutAsync).RequireAuthorization();
        endpoints.MapGet("/auth/me", MeAsync).RequireAuthorization();
        endpoints.MapPost("/auth/sessions/list", ListSessionsAsync).RequireAuthorization();
        endpoints.MapPost("/auth/sessions/revoke-others", RevokeOthersAsync).RequireAuthorization();
        endpoints.MapDelete("/auth/sessions/{id}", RevokeSessionAsync).RequireAuthorization();
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        ISessionService sessions,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var refreshToken = body.RequiredString("refreshToken", min: 1);
        body.ThrowIfInvalid();

        var tokens = await sessions
            .RotateAsync(refreshToken!, context.SessionMeta(), cancellationToken)
            .ConfigureAwait(false);

        // Unknown, reused (the family has just been revoked), expired and orphaned
        // all arrive here as null and produce one identical body.
        if (tokens is null)
        {
            throw AuthErrors.InvalidRefreshToken();
        }

        // NOTE: suspension is enforced inside RotateAsync, not here — it already
        // loads the profile, so checking there costs no extra read. See
        // SessionService.RotateAsync.

        // NOTE: tokens only. No `user` key, unlike signup/signin/magic-consume.
        return Results.Json(new AuthTokensResponse { Tokens = tokens });
    }

    /// <summary>
    /// Revokes one refresh token. The body is parsed leniently and a FAILURE IS
    /// SILENTLY IGNORED — an absent, empty or malformed body still returns 204 and
    /// simply revokes nothing, as does an unknown token.
    /// </summary>
    private static async Task<IResult> SignoutAsync(
        HttpContext context,
        ISessionRepository sessionRows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var refreshToken = body.RequiredString("refreshToken", min: 1);

        if (body.IsValid && refreshToken is not null)
        {
            await sessionRows
                .RevokeByHashAsync(AuthTokenHashing.HashToken(refreshToken), clock.GetUtcNow().UtcDateTime, cancellationToken)
                .ConfigureAwait(false);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        HttpContext context,
        IUserProfileRepository users,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();
        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);

        // A still-valid token whose account has been deleted is 404, a different
        // branch from an invalid token. The frontend signs out on either.
        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        return Results.Json(new AuthUserResponse { User = user.ToDto() });
    }

    /// <summary>
    /// POST rather than GET so the caller's refresh token travels in the body and
    /// never lands in a URL or an access log. It is OPTIONAL here — supply it and
    /// the matching row comes back <c>current: true</c>; omit it and every row is
    /// <c>current: false</c>.
    /// </summary>
    private static async Task<IResult> ListSessionsAsync(
        HttpContext context,
        ISessionRepository sessionRows,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var refreshToken = body.OptionalString("refreshToken", min: 1);
        body.ThrowIfInvalid();

        var rows = await sessionRows.ListActiveAsync(caller.Id, cancellationToken).ConfigureAwait(false);
        var currentHash = refreshToken is null ? null : AuthTokenHashing.HashToken(refreshToken);

        return Results.Json(new AuthSessionsResponse
        {
            Sessions = rows.Select(row => row.ToDto(currentHash)).ToList(),
        });
    }

    /// <summary>
    /// <c>refreshToken</c> is REQUIRED here, unlike sessions/list, and names the one
    /// session to keep.
    /// </summary>
    private static async Task<IResult> RevokeOthersAsync(
        HttpContext context,
        ISessionRepository sessionRows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var refreshToken = body.RequiredString("refreshToken", min: 1);
        body.ThrowIfInvalid();

        // Ownership of the supplied token is NOT verified — only its hash is
        // excluded from the sweep. Passing someone else's token therefore revokes
        // all of the caller's own sessions. Known Node bug, replicated deliberately.
        await sessionRows
            .RevokeAllAsync(caller.Id, clock.GetUtcNow().UtcDateTime, AuthTokenHashing.HashToken(refreshToken!), cancellationToken)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSessionAsync(
        HttpContext context,
        string id,
        ISessionRepository sessionRows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        // A malformed id is short-circuited HERE rather than being allowed to become
        // a cast failure, so "zzz", a well-formed unknown id, another user's session
        // and an already-revoked one are all the same 404 session_not_found — never
        // the framework's generic not_found.
        if (!ObjectId.TryParse(id, out var sessionId))
        {
            throw AuthErrors.SessionNotFound();
        }

        var revoked = await sessionRows
            .RevokeOwnedAsync(sessionId, caller.Id, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);

        if (!revoked)
        {
            throw AuthErrors.SessionNotFound();
        }

        return Results.NoContent();
    }
}
