using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

public interface IUserProfileRepository
{
    Task<UserProfileDocument?> FindByIdAsync(ObjectId id, CancellationToken cancellationToken = default);

    Task<UserProfileDocument?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<bool> EmailExistsAsync(string email, ObjectId? excluding = null, CancellationToken cancellationToken = default);

    Task InsertAsync(UserProfileDocument document, CancellationToken cancellationToken = default);

    Task ReplaceAsync(UserProfileDocument document, CancellationToken cancellationToken = default);

    /// <summary>Stamps <c>emailVerifiedAt</c> and bumps <c>updatedAt</c>.</summary>
    Task SetEmailVerifiedAsync(ObjectId id, DateTime at, CancellationToken cancellationToken = default);

    Task SetPendingEmailAsync(ObjectId id, string? pendingEmail, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>The email-change swap: new address, pending cleared, verified stamped.</summary>
    Task SwapEmailAsync(ObjectId id, string email, DateTime now, CancellationToken cancellationToken = default);

    Task SetPasswordMarkerAsync(ObjectId id, string? marker, DateTime now, CancellationToken cancellationToken = default);

    Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default);
}

public sealed class UserProfileRepository
    : MongoRepositoryBase<UserProfileDocument>, IUserProfileRepository
{
    /// <summary>
    /// What goes in <c>UserProfileDocument.PasswordHash</c>. The REAL hash stays in
    /// ASP.NET Identity; Mongo carries only a presence marker, because the single
    /// thing the wire needs from it is the derived <c>hasPassword</c> boolean and a
    /// SQL round trip to answer that on every <c>/auth/me</c> would be absurd.
    /// Never treat this value as a credential.
    /// </summary>
    public const string PasswordPresentMarker = "identity";

    public UserProfileRepository(IMongoDatabase database)
        : base(database, MongoCollections.Users)
    {
    }

    public Task<UserProfileDocument?> FindByIdAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        Collection.Find(Filter.Eq(u => u.Id, id)).FirstOrDefaultAsync(cancellationToken)!;

    public Task<UserProfileDocument?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        Collection.Find(Filter.Eq(u => u.Email, email)).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<bool> EmailExistsAsync(
        string email,
        ObjectId? excluding = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Filter.Eq(u => u.Email, email);
        if (excluding is { } id)
        {
            filter = Filter.And(filter, Filter.Ne(u => u.Id, id));
        }

        return await Collection.Find(filter).AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task InsertAsync(UserProfileDocument document, CancellationToken cancellationToken = default) =>
        Collection.InsertOneAsync(document, cancellationToken: cancellationToken);

    public Task ReplaceAsync(UserProfileDocument document, CancellationToken cancellationToken = default) =>
        Collection.ReplaceOneAsync(Filter.Eq(u => u.Id, document.Id), document, cancellationToken: cancellationToken);

    public Task SetEmailVerifiedAsync(ObjectId id, DateTime at, CancellationToken cancellationToken = default) =>
        Collection.UpdateOneAsync(
            Filter.Eq(u => u.Id, id),
            Update.Set(u => u.EmailVerifiedAt, at).Set(u => u.UpdatedAt, at),
            cancellationToken: cancellationToken);

    /// <summary>
    /// A null <paramref name="pendingEmail"/> <c>$unset</c>s the field rather than
    /// writing null — the wire contract omits it, and an explicit null would
    /// serialise as <c>"pendingEmail": null</c>.
    /// </summary>
    public Task SetPendingEmailAsync(
        ObjectId id,
        string? pendingEmail,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var update = pendingEmail is null
            ? Update.Unset(u => u.PendingEmail).Set(u => u.UpdatedAt, now)
            : Update.Set(u => u.PendingEmail, pendingEmail).Set(u => u.UpdatedAt, now);

        return Collection.UpdateOneAsync(Filter.Eq(u => u.Id, id), update, cancellationToken: cancellationToken);
    }

    public Task SwapEmailAsync(ObjectId id, string email, DateTime now, CancellationToken cancellationToken = default) =>
        Collection.UpdateOneAsync(
            Filter.Eq(u => u.Id, id),
            Update.Set(u => u.Email, email)
                .Unset(u => u.PendingEmail)
                .Set(u => u.EmailVerifiedAt, now)
                .Set(u => u.UpdatedAt, now),
            cancellationToken: cancellationToken);

    public Task SetPasswordMarkerAsync(
        ObjectId id,
        string? marker,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var update = marker is null
            ? Update.Unset(u => u.PasswordHash).Set(u => u.UpdatedAt, now)
            : Update.Set(u => u.PasswordHash, marker).Set(u => u.UpdatedAt, now);

        return Collection.UpdateOneAsync(Filter.Eq(u => u.Id, id), update, cancellationToken: cancellationToken);
    }

    public Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        Collection.DeleteOneAsync(Filter.Eq(u => u.Id, id), cancellationToken);
}
