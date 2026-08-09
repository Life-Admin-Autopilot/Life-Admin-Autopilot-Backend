using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.Auth;

/// <summary>
/// Every error this slice can emit, as a factory per (status, code, message).
///
/// <para>
/// <b>These strings are the contract.</b> They are compared byte-for-byte by the
/// parity harness, punctuation and capitalisation included. They are gathered
/// here rather than inlined so that the collisions are impossible to miss: three
/// different messages share the code <c>invalid_credentials</c>, two share
/// <c>invalid_code</c>, two share <c>invalid_reset_token</c> and two share
/// <c>invalid_verification_token</c>. Picking the wrong one of a pair produces a
/// response with the right status and the right code that is still wrong.
/// </para>
/// </summary>
public static class AuthErrors
{
    // ---- 401 invalid_credentials — THREE distinct messages -----------------

    /// <summary>signin. Covers unknown email, passwordless account and wrong password alike.</summary>
    public static AppException WrongEmailOrPassword() =>
        AppException.Unauthorized("invalid_credentials", "Wrong email or password.");

    /// <summary>change-password only.</summary>
    public static AppException CurrentPasswordIncorrect() =>
        AppException.Unauthorized("invalid_credentials", "Your current password is incorrect.");

    /// <summary>change-email (and DELETE /me, which belongs to the account slice).</summary>
    public static AppException PasswordIncorrect() =>
        AppException.Unauthorized("invalid_credentials", "That password is incorrect.");

    // ---- 400 invalid_code — TWO messages, one WITH details -----------------

    /// <summary>
    /// The zod shape failure. CARRIES <c>details</c> in the flatten shape, and the
    /// same sentence appears both as the envelope message and inside
    /// <c>fieldErrors.code</c>.
    /// </summary>
    public static AppException CodeFormat(IEnumerable<ValidationIssue> issues) =>
        AppException.BadRequest("invalid_code", "Enter the 6-digit code.", ValidationDetails.AsFlattened(issues));

    /// <summary>
    /// The code was well formed but unusable — wrong digits, expired, already
    /// consumed, or minted for another user or purpose. NO <c>details</c>.
    /// </summary>
    public static AppException CodeRejected() =>
        AppException.BadRequest("invalid_code", "That code is wrong or has expired. Send a new one.");

    // ---- 400 link-token failures — each code has a second "user gone" message ----

    public static AppException InvalidVerificationToken() =>
        AppException.BadRequest("invalid_verification_token", "This verification link is invalid or has expired.");

    /// <summary>Same code as above, different message: the token was valid, the account is gone.</summary>
    public static AppException VerificationTokenUserGone() =>
        AppException.BadRequest("invalid_verification_token", "Account not found.");

    public static AppException InvalidResetToken() =>
        AppException.BadRequest(
            "invalid_reset_token",
            "This reset link is invalid or has expired. Please request a new one.");

    /// <summary>Same code as above, different message.</summary>
    public static AppException ResetTokenUserGone() =>
        AppException.BadRequest("invalid_reset_token", "Account not found.");

    public static AppException InvalidMagicToken() =>
        AppException.BadRequest("invalid_magic_token", "This sign-in link is invalid or has expired. Request a new one.");

    // ---- 400 flow errors ----------------------------------------------------

    /// <summary>change-password on a magic-link-only account — and also when the user row is gone.</summary>
    public static AppException NoPasswordSet() =>
        AppException.BadRequest("no_password_set", "This account has no password to change.");

    public static AppException PasswordRequiredForEmailChange() =>
        AppException.BadRequest("password_required", "Enter your password to change your email.");

    public static AppException EmailUnchanged() =>
        AppException.BadRequest("email_unchanged", "That's already your email address.");

    public static AppException NoPendingEmail() =>
        AppException.BadRequest("no_pending_email", "There's no email change waiting.");

    public static AppException EmailSendFailed() =>
        AppException.BadRequest("email_send_failed", "We could not send that email. Try again in a moment.");

    /// <summary>change-email's zod failure. Flatten details, its own code and message.</summary>
    public static AppException InvalidEmailBody(IEnumerable<ValidationIssue> issues) =>
        AppException.BadRequest(
            "invalid_body",
            "That didn't look like an email address.",
            ValidationDetails.AsFlattened(issues));

    // ---- 401 / 404 / 409 ----------------------------------------------------

    /// <summary>Every refresh failure mode — unknown, reused, expired, orphaned — is this one body.</summary>
    public static AppException InvalidRefreshToken() =>
        AppException.Unauthorized("invalid_refresh_token", "This session has expired. Please sign in again.");

    public static AppException UserNotFound() =>
        AppException.NotFound("user_not_found", "Account no longer exists.");

    public static AppException SessionNotFound() =>
        AppException.NotFound("session_not_found", "That session is no longer active.");

    public static AppException EmailTaken() =>
        AppException.Conflict("email_taken", "An account with this email already exists.");
}
