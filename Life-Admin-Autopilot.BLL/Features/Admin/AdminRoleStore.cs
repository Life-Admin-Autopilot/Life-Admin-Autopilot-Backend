using Life_Admin_Autopilot.DAL.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>Reads and grants console roles on the existing Identity tables.</summary>
public interface IAdminRoleStore
{
    /// <summary>Console roles this identity holds. Empty for an ordinary customer, which is nearly everyone.</summary>
    Task<IReadOnlyList<string>> RolesForAsync(Guid identityUserId, CancellationToken cancellationToken = default);

    /// <summary>Creates <c>Admin</c> and <c>Support</c> if they do not exist. Idempotent.</summary>
    Task EnsureRolesExistAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants a role to an existing account, by email. Returns false when no such
    /// account exists — the console never creates credentials, so an invite is
    /// "sign up in the app, then be granted here".
    /// </summary>
    Task<bool> GrantAsync(string email, string role, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(string email, string role, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Email, string Role)>> ListAdminsAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdminRoleStore"/>
public sealed class AdminRoleStore : IAdminRoleStore
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<IdentityRole<Guid>> _roles;
    private readonly ILogger<AdminRoleStore> _logger;

    public AdminRoleStore(
        UserManager<ApplicationUser> users,
        RoleManager<IdentityRole<Guid>> roles,
        ILogger<AdminRoleStore> logger)
    {
        _users = users;
        _roles = roles;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> RolesForAsync(
        Guid identityUserId,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(identityUserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return Array.Empty<string>();
        }

        var roles = await _users.GetRolesAsync(user).ConfigureAwait(false);

        // Intersected with the console vocabulary rather than returned raw: a role
        // added for some other purpose must not become console access by accident.
        return roles.Where(r => AdminRoles.All.Contains(r, StringComparer.Ordinal)).ToList();
    }

    public async Task EnsureRolesExistAsync(CancellationToken cancellationToken = default)
    {
        foreach (var role in AdminRoles.All)
        {
            if (await _roles.RoleExistsAsync(role).ConfigureAwait(false))
            {
                continue;
            }

            var result = await _roles.CreateAsync(new IdentityRole<Guid>(role)).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _logger.LogError(
                    "admin:role-create-failed role={Role} errors={Errors}",
                    role,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }
    }

    public async Task<bool> GrantAsync(string email, string role, CancellationToken cancellationToken = default)
    {
        if (!AdminRoles.All.Contains(role, StringComparer.Ordinal))
        {
            return false;
        }

        var user = await _users.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        await EnsureRolesExistAsync(cancellationToken).ConfigureAwait(false);

        if (await _users.IsInRoleAsync(user, role).ConfigureAwait(false))
        {
            return true;
        }

        var result = await _users.AddToRoleAsync(user, role).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task<bool> RevokeAsync(string email, string role, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        var result = await _users.RemoveFromRoleAsync(user, role).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task<IReadOnlyList<(string Email, string Role)>> ListAdminsAsync(
        CancellationToken cancellationToken = default)
    {
        var found = new List<(string, string)>();

        foreach (var role in AdminRoles.All)
        {
            var members = await _users.GetUsersInRoleAsync(role).ConfigureAwait(false);
            found.AddRange(members.Select(m => (m.Email ?? string.Empty, role)));
        }

        return found;
    }
}
