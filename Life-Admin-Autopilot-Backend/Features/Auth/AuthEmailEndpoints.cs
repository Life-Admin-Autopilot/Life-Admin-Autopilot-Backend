using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;

namespace Life_Admin_Autopilot_Backend.Features.Auth;

/// <summary>
/// Port of <c>routes/auth.email.ts</c> and <c>routes/auth.emailChange.ts</c>.
///
/// <para>
/// Four of these routes mirror <c>safeParse</c> rather than the throwing
/// <c>.parse()</c>, so their validation failures carry the FLATTEN details shape
/// under the route's own code — not <c>validation_error</c> with an issue array.
/// </para>
/// </summary>
public static class AuthEmailEndpoints
{
    public static void MapAuthEmailEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/verify-email", VerifyEmailAsync)
            .AllowAnonymous()
            .RateLimited(KernelRateLimiters.Auth);

        endpoints.MapPost("/auth/verify-email/send-code", SendCodeAsync)
            .RequireAuthorization()
            .RateLimited(KernelRateLimiters.StrictAuth);

        endpoints.MapPost("/auth/verify-email/confirm-code", ConfirmCodeAsync)
            .RequireAuthorization()
            .RateLimited(KernelRateLimiters.StrictAuth);

        endpoints.MapPost("/auth/change-email", RequestEmailChangeAsync)
            .RequireAuthorization()
            .RateLimited(KernelRateLimiters.StrictAuth);

        // NOT rate limited, unlike its POST sibling.
        endpoints.MapDelete("/auth/change-email", CancelEmailChangeAsync).RequireAuthorization();

        endpoints.MapPost("/auth/change-email/confirm", ConfirmEmailChangeAsync)
            .RequireAuthorization()
            .RateLimited(KernelRateLimiters.StrictAuth);
    }

    /// <summary>The mailed-link path. Unauthenticated — the token itself is the proof.</summary>
    private static async Task<IResult> VerifyEmailAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthFlowService flows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var token = body.RequiredString("token", min: 1);
        body.ThrowIfInvalid();

        var userId = await flows
            .ConsumeLinkTokenAsync(token!, VerificationPurposes.EmailVerification, cancellationToken)
            .ConfigureAwait(false);

        if (userId is null)
        {
            throw AuthErrors.InvalidVerificationToken();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await users.SetEmailVerifiedAsync(userId.Value, now, cancellationToken).ConfigureAwait(false);

        var user = await users.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        // Same code as the branch above, different message. Note this is a 400 —
        // /auth/magic-consume answers 404 for the identical situation.
        if (user is null)
        {
            throw AuthErrors.VerificationTokenUserGone();
        }

        return Results.Json(new AuthUserResponse { User = user.ToDto() });
    }

    /// <summary>
    /// Takes NO body. Already-verified is a deliberate no-op 204 that sends no
    /// mail, and is indistinguishable from a successful send.
    /// </summary>
    private static async Task<IResult> SendCodeAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthFlowService flows,
        IAuthEmailSender email,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();
        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        if (user.EmailVerifiedAt is not null)
        {
            return Results.NoContent();
        }

        var code = await flows
            .IssueCodeAsync(user.Id, VerificationPurposes.EmailVerificationCode, cancellationToken)
            .ConfigureAwait(false);

        // AWAITED, unlike signup's fire-and-forget: here a mail failure IS the
        // client's problem, because there is nothing else to tell them.
        try
        {
            await email.SendCodeAsync(user.Email, code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(typeof(AuthEmailEndpoints))
                .LogWarning(ex, "auth:verify-email:code-email-failed");

            throw AuthErrors.EmailSendFailed();
        }

        return Results.NoContent();
    }

    /// <summary>
    /// safeParse lane. The two 400s share the code <c>invalid_code</c>: a FORMAT
    /// failure carries flatten details, a REJECTED code carries none and says
    /// something different.
    /// </summary>
    private static async Task<IResult> ConfirmCodeAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthFlowService flows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var code = body.SixDigitCode("code");

        if (!body.IsValid)
        {
            throw AuthErrors.CodeFormat(body.Issues);
        }

        var accepted = await flows
            .ConsumeCodeAsync(caller.Id, VerificationPurposes.EmailVerificationCode, code!, cancellationToken)
            .ConfigureAwait(false);

        if (!accepted)
        {
            throw AuthErrors.CodeRejected();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await users.SetEmailVerifiedAsync(caller.Id, now, cancellationToken).ConfigureAwait(false);

        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        return Results.Json(new AuthUserResponse { User = user.ToDto() });
    }

    /// <summary>
    /// Step 1. The account keeps authenticating as the OLD address — the new one is
    /// only parked in <c>pendingEmail</c> — so a typo or a hostile address can
    /// never lock the owner out. The code goes to the NEW address.
    /// </summary>
    private static async Task<IResult> RequestEmailChangeAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthCredentialStore credentials,
        IAuthFlowService flows,
        IAuthEmailSender email,
        ILoggerFactory loggerFactory,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var newEmail = body.Email("newEmail");
        var password = body.OptionalString("password", min: 1);

        if (!body.IsValid)
        {
            throw AuthErrors.InvalidEmailBody(body.Issues);
        }

        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        if (string.Equals(newEmail, user.Email, StringComparison.Ordinal))
        {
            throw AuthErrors.EmailUnchanged();
        }

        // A magic-link-only account skips the password gate entirely — it has no
        // password to offer.
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            if (password is null)
            {
                throw AuthErrors.PasswordRequiredForEmailChange();
            }

            var ok = await credentials
                .VerifyPasswordAsync(user.IdentityUserId, password, cancellationToken)
                .ConfigureAwait(false);

            if (!ok)
            {
                throw AuthErrors.PasswordIncorrect();
            }
        }

        if (await users.EmailExistsAsync(newEmail!, excluding: user.Id, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw AuthErrors.EmailTaken();
        }

        // pendingEmail is persisted BEFORE the send is attempted, so an
        // email_send_failed still leaves the pending address parked on the account.
        var now = clock.GetUtcNow().UtcDateTime;
        await users.SetPendingEmailAsync(user.Id, newEmail, now, cancellationToken).ConfigureAwait(false);

        var code = await flows
            .IssueCodeAsync(user.Id, VerificationPurposes.EmailChange, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await email.SendCodeAsync(newEmail!, code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(typeof(AuthEmailEndpoints))
                .LogWarning(ex, "auth:change-email:email-failed");

            throw AuthErrors.EmailSendFailed();
        }

        var updated = await users.FindByIdAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return Results.Json(new AuthUserResponse { User = (updated ?? user).ToDto() });
    }

    /// <summary>
    /// Idempotent: clears <c>pendingEmail</c> and answers 204 whether or not one was
    /// pending. Does NOT delete the outstanding code — confirm rejects with
    /// <c>no_pending_email</c> before it would ever be consulted.
    /// </summary>
    private static async Task<IResult> CancelEmailChangeAsync(
        HttpContext context,
        IUserProfileRepository users,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();
        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        await users
            .SetPendingEmailAsync(user.Id, null, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>Step 2 — redeem the code and swap the address.</summary>
    private static async Task<IResult> ConfirmEmailChangeAsync(
        HttpContext context,
        IUserProfileRepository users,
        IAuthCredentialStore credentials,
        IAuthFlowService flows,
        ISessionRepository sessionRows,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = new ZodBody(await context.ReadBodyAsync(cancellationToken));
        var code = body.SixDigitCode("code");
        var refreshToken = body.OptionalString("refreshToken", min: 1);

        if (!body.IsValid)
        {
            throw AuthErrors.CodeFormat(body.Issues);
        }

        var user = await users.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw AuthErrors.UserNotFound();
        }

        // Checked BEFORE the code, so a confirm with nothing pending never consumes
        // the code it was given.
        if (string.IsNullOrEmpty(user.PendingEmail))
        {
            throw AuthErrors.NoPendingEmail();
        }

        var accepted = await flows
            .ConsumeCodeAsync(user.Id, VerificationPurposes.EmailChange, code!, cancellationToken)
            .ConfigureAwait(false);

        if (!accepted)
        {
            throw AuthErrors.CodeRejected();
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // RE-CHECK uniqueness: someone may have claimed the address during the 15
        // minutes the code was live. On conflict the pending address is CLEARED and
        // the code has already been burnt, so the user must restart step 1.
        if (await users.EmailExistsAsync(user.PendingEmail, excluding: user.Id, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await users.SetPendingEmailAsync(user.Id, null, now, cancellationToken).ConfigureAwait(false);
            throw AuthErrors.EmailTaken();
        }

        // Redeeming a code sent to that address IS proof of control, so the new
        // address is verified outright.
        await users.SwapEmailAsync(user.Id, user.PendingEmail, now, cancellationToken).ConfigureAwait(false);
        await credentials.SetEmailAsync(user.IdentityUserId, user.PendingEmail, cancellationToken).ConfigureAwait(false);

        // Present -> keep that one session. ABSENT -> revoke NOTHING, which is the
        // opposite of change-password's blanket revoke.
        if (refreshToken is not null)
        {
            await sessionRows
                .RevokeAllAsync(user.Id, now, AuthTokenHashing.HashToken(refreshToken), cancellationToken)
                .ConfigureAwait(false);
        }

        var updated = await users.FindByIdAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            throw AuthErrors.UserNotFound();
        }

        return Results.Json(new AuthUserResponse { User = updated.ToDto() });
    }
}
