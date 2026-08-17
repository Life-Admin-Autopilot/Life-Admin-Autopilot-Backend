using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;

namespace Life_Admin_Autopilot_Backend.Features.Auth;

/// <summary>Port of <c>server/src/routes/auth.password.ts</c>.</summary>
public static class AuthPasswordEndpoints
{
    public static void MapAuthPasswordEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/signup", SignupAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.Auth);

        endpoints.MapPost("/auth/signin", SigninAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.Auth);

        endpoints.MapPost("/auth/forgot-password", ForgotPasswordAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.StrictAuth);

        endpoints.MapPost("/auth/reset-password", ResetPasswordAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.StrictAuth);

        // requireAuth BEFORE strictAuthLimiter: an unauthenticated call is rejected
        // without spending a rate-limit slot. The auth middleware runs ahead of
        // endpoint filters, so this ordering is structural.
        endpoints.MapPost("/auth/change-password", ChangePasswordAsync)
            .RequireAuthorization()
            .RateLimited(KernelRateLimiters.StrictAuth);
    }

    private static async Task<IResult> SignupAsync(
        HttpContext context,
        IUserProfileRepository users,
        IUserProvisioningService provisioning,
        ISessionService sessions,
        IAuthFlowService flows,
        IAuthEmailSender email,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var address = body.Email("email");
        var password = body.RequiredString("password", min: 8, max: 128);
        body.ThrowIfInvalid();

        if (await users.EmailExistsAsync(address!, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw AuthErrors.EmailTaken();
        }

        var user = await provisioning.CreateAsync(address!, password, cancellationToken).ConfigureAwait(false);
        var tokens = await sessions.IssueAsync(user, null, context.SessionMeta(), cancellationToken).ConfigureAwait(false);

        // FIRE AND FORGET. A mail failure is logged and must never turn this 201
        // into an error — which is also why the returned user has no
        // emailVerifiedAt.
        _ = SendVerificationAsync(user.Id, user.Email, flows, email, loggerFactory);

        return Results.Json(
            new AuthSessionResponse { User = user.ToDto(), Tokens = tokens },
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task SendVerificationAsync(
        MongoDB.Bson.ObjectId userId,
        string address,
        IAuthFlowService flows,
        IAuthEmailSender email,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var raw = await flows
                .IssueLinkTokenAsync(userId, VerificationPurposes.EmailVerification, AuthTtl.EmailVerificationLink)
                .ConfigureAwait(false);

            await email.SendVerificationLinkAsync(address, raw).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(typeof(AuthPasswordEndpoints))
                .LogWarning(ex, "auth:signup:verification-email-failed");
        }
    }

    private static async Task<IResult> SigninAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthCredentialStore credentials,
        ISessionService sessions,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var address = body.Email("email");

        // min(1) and NO max here — signup and reset bound the password at 128, this
        // route deliberately does not.
        var password = body.RequiredString("password", min: 1);
        body.ThrowIfInvalid();

        var user = await users.FindByEmailAsync(address!, cancellationToken).ConfigureAwait(false);

        // Unknown address, magic-link-only account and wrong password are ONE
        // indistinguishable 401 — anything else enumerates accounts.
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            throw AuthErrors.WrongEmailOrPassword();
        }

        var ok = await credentials
            .VerifyPasswordAsync(user.IdentityUserId, password!, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            throw AuthErrors.WrongEmailOrPassword();
        }

        // Suspension, checked AFTER the password.
        //
        // Order matters and this order is deliberate: refusing a suspended account
        // before verifying its password would answer differently for "suspended
        // account, wrong password" than for "unknown address", which turns the
        // suspension into an account-enumeration oracle. Checking it second means
        // only someone who already proved they own the account learns it is
        // suspended — which is exactly who should be told.
        //
        // Parity: SuspendedAt is null on every account the reference server writes,
        // so this branch is unreachable in a parity run.
        AdminSuspension.ThrowIfSuspended(user);

        var tokens = await sessions.IssueAsync(user, null, context.SessionMeta(), cancellationToken).ConfigureAwait(false);

        return Results.Json(new AuthSessionResponse { User = user.ToDto(), Tokens = tokens });
    }

    private static async Task<IResult> ForgotPasswordAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthFlowService flows,
        IAuthEmailSender email,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var address = body.Email("email");
        body.ThrowIfInvalid();

        var user = await users.FindByEmailAsync(address!, cancellationToken).ConfigureAwait(false);

        // ALWAYS 204, account or no account. The only thing that can produce a
        // different status is a malformed address, which is a syntax fact rather
        // than an account fact.
        if (user is not null)
        {
            try
            {
                var raw = await flows
                    .IssueLinkTokenAsync(user.Id, VerificationPurposes.PasswordReset, AuthTtl.PasswordReset, cancellationToken)
                    .ConfigureAwait(false);

                _ = email.SendPasswordResetAsync(user.Email, raw, CancellationToken.None);
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger(typeof(AuthPasswordEndpoints))
                    .LogWarning(ex, "auth:forgot-password:email-failed");
            }
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthCredentialStore credentials,
        IAuthFlowService flows,
        ISessionRepository sessionRows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var token = body.RequiredString("token", min: 1);
        var newPassword = body.RequiredString("newPassword", min: 8, max: 128);
        body.ThrowIfInvalid();

        var userId = await flows
            .ConsumeLinkTokenAsync(token!, VerificationPurposes.PasswordReset, cancellationToken)
            .ConfigureAwait(false);

        if (userId is null)
        {
            throw AuthErrors.InvalidResetToken();
        }

        var user = await users.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        // Same code as above, DIFFERENT message. The token is already burnt at this
        // point — consumption happens before the user lookup.
        if (user is null)
        {
            throw AuthErrors.ResetTokenUserGone();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await credentials.SetPasswordAsync(user.IdentityUserId, newPassword!, cancellationToken).ConfigureAwait(false);
        await users.SetPasswordMarkerAsync(user.Id, UserProfileRepository.PasswordPresentMarker, now, cancellationToken)
            .ConfigureAwait(false);

        // EVERY session, including the caller's. There is no keep-this-device
        // escape hatch on this route, unlike change-password.
        await sessionRows.RevokeAllAsync(user.Id, now, exceptHash: null, cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthCredentialStore credentials,
        ISessionRepository sessionRows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var currentPassword = body.RequiredString("currentPassword", min: 1);
        var newPassword = body.RequiredString("newPassword", min: 8, max: 128);
        var refreshToken = body.OptionalString("refreshToken", min: 1);

        // The cross-field .refine() runs ONLY when the base object parsed cleanly,
        // and reports on path `newPassword`. It travels the global ZodError lane,
        // so it emits validation_error with the ISSUE-ARRAY details — not
        // invalid_body.
        if (body.IsValid && currentPassword == newPassword)
        {
            body.AddIssue("newPassword", "New password must be different from your current one.");
        }

        body.ThrowIfInvalid();

        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);

        // A MISSING user takes this branch too — 400 no_password_set, not 404.
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            throw AuthErrors.NoPasswordSet();
        }

        var ok = await credentials
            .VerifyPasswordAsync(user.IdentityUserId, currentPassword!, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            throw AuthErrors.CurrentPasswordIncorrect();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await credentials.SetPasswordAsync(user.IdentityUserId, newPassword!, cancellationToken).ConfigureAwait(false);
        await users.SetPasswordMarkerAsync(user.Id, UserProfileRepository.PasswordPresentMarker, now, cancellationToken)
            .ConfigureAwait(false);

        // Supplying the caller's own refresh token keeps THAT session alive and
        // kills the rest. Omitting it signs the caller out everywhere, including
        // here. change-email/confirm makes the opposite choice when it is omitted.
        await sessionRows.RevokeAllAsync(user.Id, now, refreshToken is null ? null : AuthTokenHashing.HashToken(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        return Results.NoContent();
    }
}
