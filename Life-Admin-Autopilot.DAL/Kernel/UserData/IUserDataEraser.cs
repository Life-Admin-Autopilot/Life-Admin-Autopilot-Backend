using MongoDB.Bson;

namespace Life_Admin_Autopilot.DAL.Kernel.UserData;

/// <summary>Everything an eraser needs to find one user's rows.</summary>
/// <param name="UserId">The Mongo ObjectId every <c>userId</c> reference stores.</param>
/// <param name="IdentityUserId">The SQL Identity key, for credential-side cleanup.</param>
public readonly record struct UserErasureContext(ObjectId UserId, Guid IdentityUserId);

/// <summary>
/// Conventional <see cref="IUserDataEraser.Order"/> values. Lower runs first.
/// </summary>
public static class UserErasureOrder
{
    /// <summary>
    /// Blob stores. MUST run before the rows that point at the blobs vanish —
    /// once the row is gone the storage key is unrecoverable and the bytes leak
    /// forever.
    /// </summary>
    public const int Storage = 100;

    /// <summary>Session revocation, before the refresh-token rows go.</summary>
    public const int Sessions = 200;

    /// <summary>Everything that references the user. The default.</summary>
    public const int Dependents = 300;

    /// <summary>
    /// The user row itself. Reserved for the kernel — the account is retired only
    /// once every dependent collection is gone, so a mid-cascade failure leaves a
    /// still-authenticated user (re-runnable) rather than a logged-out account
    /// with orphaned data.
    /// </summary>
    public const int Account = 1000;
}

/// <summary>
/// One slice's contribution to "delete my account".
///
/// <para>
/// Node keeps a single hand-maintained 12-collection list inside
/// <c>routes/me.ts</c>. Seven agents editing that one function is a guaranteed
/// merge conflict AND a guaranteed omission, so the kernel inverts it: each slice
/// registers its OWN eraser and the kernel runs them in
/// <see cref="Order"/> sequence.
/// </para>
///
/// <para><b>Registration:</b> inside your <c>AddXxxFeature()</c> extension —
/// <c>services.AddUserDataEraser&lt;MyEraser&gt;();</c></para>
///
/// <para><b>Contract:</b> be idempotent (the cascade is re-runnable), never
/// throw for "already gone", and log-and-continue on best-effort external
/// cleanup — a storage miss must not abort account deletion.</para>
/// </summary>
public interface IUserDataEraser
{
    /// <summary>Diagnostic label, e.g. <c>"tasks"</c>. Appears in the cascade log.</summary>
    string Name { get; }

    /// <summary>See <see cref="UserErasureOrder"/>. Default to <c>Dependents</c>.</summary>
    int Order => UserErasureOrder.Dependents;

    Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default);
}
