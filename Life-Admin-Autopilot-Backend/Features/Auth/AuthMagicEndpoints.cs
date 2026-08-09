using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;

namespace Life_Admin_Autopilot_Backend.Features.Auth;

/// <summary>Port of <c>server/src/routes/auth.magic.ts</c>.</summary>
public static class AuthMagicEndpoints
{
    public static void MapAuthMagicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/magic-link", RequestMagicLinkAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.StrictAuth);

        endpoints.MapPost("/auth/magic-consume", ConsumeMagicLinkAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.Auth);
    }

    /// <summary>
    /// AUTO-CREATES the account on first request, so passwordless onboarding needs
    /// no separate signup. Always 204 — the response cannot distinguish an existing
    /// account from a new one, which is what keeps it enumeration-safe.
    /// </summary>
    private static async Task<IResult> RequestMagicLinkAsync(
        HttpContext context,
        IUserProfileRepository users,
        IUserProvisioningService provisioning,
        IAuthFlowService flows,
        IAuthEmailSender email,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var address = body.Email("email");
        body.ThrowIfInvalid();

        // The account is created even if the mail then fails — the send is
        // fire-and-forget and cannot roll this back.
        var user = await users.FindByEmailAsync(address!, cancellationToken).ConfigureAwait(false)
            ?? await provisioning.CreateAsync(address!, null, cancellationToken).ConfigureAwait(false);

        try
        {
            var raw = await flows
                .IssueLinkTokenAsync(user.Id, VerificationPurposes.MagicLink, AuthTtl.MagicLink, cancellationToken)
                .ConfigureAwait(false);

            _ = email.SendMagicLinkAsync(user.Email, raw, CancellationToken.None);
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(typeof(AuthMagicEndpoints))
                .LogWarning(ex, "auth:magic-link:email-failed");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ConsumeMagicLinkAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthFlowService flows,
        ISessionService sessions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var token = body.RequiredString("token", min: 1);
        body.ThrowIfInvalid();

        var userId = await flows
            .ConsumeLinkTokenAsync(token!, VerificationPurposes.MagicLink, cancellationToken)
            .ConfigureAwait(false);

        if (userId is null)
        {
            throw AuthErrors.InvalidMagicToken();
        }

        var user = await users.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        // 404 here, where /auth/verify-email and /auth/reset-password answer 400 for
        // the very same "token was good, account is gone" case. The inconsistency is
        // the reference's and is reproduced deliberately.
        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        // Redeeming the link proves mailbox control, so it verifies the address —
        // but only when it was not already verified. An existing timestamp is kept.
        if (user.EmailVerifiedAt is null)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            await users.SetEmailVerifiedAsync(user.Id, now, cancellationToken).ConfigureAwait(false);
            user.EmailVerifiedAt = now;
            user.UpdatedAt = now;
        }

        var tokens = await sessions.IssueAsync(user, null, context.SessionMeta(), cancellationToken).ConfigureAwait(false);

        return Results.Json(new AuthSessionResponse { User = user.ToDto(), Tokens = tokens });
    }
}
