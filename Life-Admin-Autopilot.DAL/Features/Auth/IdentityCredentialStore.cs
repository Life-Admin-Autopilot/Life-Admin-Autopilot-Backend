using Life_Admin_Autopilot.DAL.Data;
using Life_Admin_Autopilot.DAL.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

/// <summary>
/// The ASP.NET Identity implementation of <see cref="IAuthCredentialStore"/> —
/// the approved architecture: credentials in Identity on SQL, profile in Mongo.
///
/// <para>
/// <b>Why this drives the DbContext and hasher directly instead of using
/// <c>UserManager</c>.</b> <c>AddIdentityCore</c> is configured with the default
/// password policy, which additionally demands a digit, an upper- and a
/// lower-case letter and a non-alphanumeric character. The Node reference
/// enforces <em>only</em> length 8..128, so <c>"password"</c> is accepted there
/// and would be rejected by <c>UserManager.CreateAsync</c> — a 201 turning into
/// an error, which is a parity break. Routing around the validator pipeline
/// keeps Identity's schema and its password hasher while leaving the rules to
/// this slice's own validators, which mirror zod exactly. Uniqueness is likewise
/// decided by the Mongo profile lookup, so the two stores cannot disagree about
/// which address is taken.
/// </para>
///
/// <para>
/// The hash is PBKDF2 (Identity's <c>PasswordHasher</c>), not the reference's
/// argon2id. That is invisible to the contract — a hash never crosses the wire
/// and the two servers never share a credential database — but it does mean an
/// account created against Node cannot sign in against .NET, so a real migration
/// needs a rehash-on-signin step.
/// </para>
/// </summary>
public sealed class IdentityCredentialStore : IAuthCredentialStore
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher<ApplicationUser> _hasher;

    public IdentityCredentialStore(ApplicationDbContext db, IPasswordHasher<ApplicationUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<Guid> CreateAsync(
        string email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = Normalize(email),
            Email = email,
            NormalizedEmail = Normalize(email),
            EmailConfirmed = false,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };

        // Null password = a magic-link account. It has no credential at all, which
        // is exactly what makes hasPassword false.
        if (password is not null)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    public async Task<bool> VerifyPasswordAsync(
        Guid identityUserId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(identityUserId, cancellationToken).ConfigureAwait(false);

        if (user?.PasswordHash is null)
        {
            return false;
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return result == PasswordVerificationResult.Success;
    }

    public async Task SetPasswordAsync(
        Guid identityUserId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(identityUserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        user.PasswordHash = _hasher.HashPassword(user, password);

        // Rotating the stamp invalidates anything Identity derives from it.
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEmailAsync(
        Guid identityUserId,
        string email,
        CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(identityUserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        user.Email = email;
        user.NormalizedEmail = Normalize(email);
        user.UserName = email;
        user.NormalizedUserName = Normalize(email);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Idempotent — a row that is already gone is not an error.</summary>
    public async Task DeleteAsync(Guid identityUserId, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(identityUserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<ApplicationUser?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    private static string Normalize(string value) => value.ToUpperInvariant();
}
