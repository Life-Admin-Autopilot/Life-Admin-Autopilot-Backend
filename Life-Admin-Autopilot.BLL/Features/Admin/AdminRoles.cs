namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>
/// The two console roles, and the authorization policy that gates every
/// <c>/admin/*</c> route.
///
/// <para>
/// <b>Admins are ordinary Identity users who hold a role.</b> There is no second
/// credential store: the same <c>AspNetUsers</c> row that signs into the app signs
/// into the console, and the role is what separates them. That is why the token is
/// what differs — see <see cref="AdminTokenService"/> — rather than the identity.
/// </para>
/// </summary>
public static class AdminRoles
{
    /// <summary>Everything, including operations config and admin management.</summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Read, plus per-customer account state. <b>Cannot</b> change kill switches,
    /// quota configuration, or who else is an admin.
    /// </summary>
    public const string Support = "Support";

    /// <summary>Both roles. The gate on every console route.</summary>
    public const string ConsolePolicy = "AdminConsole";

    /// <summary>Admin only. The gate on operations and admin management.</summary>
    public const string OperatorPolicy = "AdminOperator";

    /// <summary>The authentication scheme admin tokens are validated under.</summary>
    public const string Scheme = "AdminBearer";

    public static readonly IReadOnlyList<string> All = new[] { Admin, Support };

    /// <summary>
    /// The single role a principal holds, for the audit row. A user in both roles
    /// records as <see cref="Admin"/> — the higher privilege is the true answer to
    /// "what could they have done?"
    /// </summary>
    public static string Highest(IEnumerable<string> roles) =>
        roles.Contains(Admin, StringComparer.Ordinal) ? Admin : Support;
}
