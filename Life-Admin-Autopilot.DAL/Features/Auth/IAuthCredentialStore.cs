namespace Life_Admin_Autopilot.DAL.Features.Auth;

/// <summary>
/// The seam between this slice and the credential store.
///
/// <para>
/// Credentials live in ASP.NET Identity on SQL; the profile lives in a Mongo
/// <c>users</c> document keyed by <c>UserProfileDocument.IdentityUserId</c>.
/// This interface is the ONLY way the auth slice reaches the SQL side, which
/// keeps the slice indifferent to which EF provider is configured
/// (<c>Database:Provider</c> = SqlServer | Sqlite) and makes the flows testable
/// without a database at all.
/// </para>
///
/// <para>
/// Nothing here returns or accepts a password HASH. The hash never leaves the
/// credential store, and it is never mirrored into Mongo — the profile document
/// carries only a presence marker so <c>hasPassword</c> can be derived without a
/// SQL round trip.
/// </para>
/// </summary>
public interface IAuthCredentialStore
{
    /// <summary>
    /// Creates the Identity row. <paramref name="password"/> is null for a
    /// magic-link account, which then has no credential at all — that is what
    /// makes <c>hasPassword</c> false and lets the client skip the
    /// re-confirmation prompt it could never satisfy.
    /// </summary>
    Task<Guid> CreateAsync(string email, string? password, CancellationToken cancellationToken = default);

    /// <summary>
    /// False for a wrong password AND for an account with no credential. Callers
    /// must not use the distinction to vary their response — signin folds both
    /// into one 401 to avoid account enumeration.
    /// </summary>
    Task<bool> VerifyPasswordAsync(Guid identityUserId, string password, CancellationToken cancellationToken = default);

    /// <summary>Replaces the credential, creating one if the account had none.</summary>
    Task SetPasswordAsync(Guid identityUserId, string password, CancellationToken cancellationToken = default);

    /// <summary>Keeps the Identity user name/email in step after an address change.</summary>
    Task SetEmailAsync(Guid identityUserId, string email, CancellationToken cancellationToken = default);

    /// <summary>Idempotent — a missing row is not an error.</summary>
    Task DeleteAsync(Guid identityUserId, CancellationToken cancellationToken = default);
}
