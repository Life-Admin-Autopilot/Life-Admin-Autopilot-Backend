using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>
/// The one place that decides what a suspended account is told.
///
/// <para>
/// Kept out of the auth slice so the customer sign-in path holds a single call
/// rather than a policy, and so the message is identical wherever it is enforced —
/// sign-in and refresh must not disagree about why a session ended.
/// </para>
/// </summary>
public static class AdminSuspension
{
    /// <summary>
    /// <c>403 account_suspended</c>. Deliberately NOT a 401: a 401 tells the client
    /// its credentials are wrong, and the app would respond by clearing the session
    /// and inviting the user to sign in again — a loop they cannot win and cannot
    /// understand. A 403 says the credentials were fine and the account is not.
    /// </summary>
    public const string ErrorCode = "account_suspended";

    public const string Message =
        "This account has been suspended. Contact support if you think that is a mistake.";

    public static bool IsSuspended(UserProfileDocument? user) => user?.SuspendedAt is not null;

    /// <summary>No-op for every account that is not suspended, which is nearly all of them.</summary>
    public static void ThrowIfSuspended(UserProfileDocument? user)
    {
        if (IsSuspended(user))
        {
            throw AppException.Forbidden(ErrorCode, Message);
        }
    }
}
