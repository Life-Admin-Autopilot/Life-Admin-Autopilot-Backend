using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

public interface ISessionRepository
{
    Task<RefreshTokenDocument> InsertAsync(RefreshTokenDocument document, CancellationToken cancellationToken = default);

    Task<RefreshTokenDocument?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RefreshTokenDocument>> ListActiveAsync(ObjectId userId, CancellationToken cancellationToken = default);

    Task MarkRotatedAsync(ObjectId id, string replacedByHash, DateTime now, CancellationToken cancellationToken = default);

    Task RevokeByHashAsync(string tokenHash, DateTime now, CancellationToken cancellationToken = default);

    Task<bool> RevokeOwnedAsync(ObjectId id, ObjectId userId, DateTime now, CancellationToken cancellationToken = default);

    Task RevokeAllAsync(ObjectId userId, DateTime now, string? exceptHash = null, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(ObjectId userId, CancellationToken cancellationToken = default);
}

public sealed class SessionRepository
    : MongoRepositoryBase<RefreshTokenDocument>, ISessionRepository
{
    public SessionRepository(IMongoDatabase database)
        : base(database, MongoCollections.RefreshTokens)
    {
    }

    /// <summary>
    /// Live means "not revoked". An EXPIRED-but-unrevoked row still matches, which
    /// is deliberate: Node lists those until Mongo's TTL monitor sweeps them, and
    /// the family-revoke sweep must reach them too.
    /// </summary>
    private static FilterDefinition<RefreshTokenDocument> NotRevoked() =>
        Filter.Exists(t => t.RevokedAt, false);

    public async Task<RefreshTokenDocument> InsertAsync(
        RefreshTokenDocument document,
        CancellationToken cancellationToken = default)
    {
        await Collection.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document;
    }

    public Task<RefreshTokenDocument?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Collection
            .Find(Filter.Eq(t => t.TokenHash, tokenHash))
            .FirstOrDefaultAsync(cancellationToken)!;

    /// <summary>
    /// Sort is <c>lastUsedAt</c> descending, then <c>createdAt</c> descending —
    /// copied from the Node route, and observable because the harness captures
    /// <c>sessions[0].id</c>.
    /// </summary>
    public async Task<IReadOnlyList<RefreshTokenDocument>> ListActiveAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.And(Filter.Eq(t => t.UserId, userId), NotRevoked()))
            .Sort(Sort.Descending(t => t.LastUsedAt).Descending(t => t.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task MarkRotatedAsync(
        ObjectId id,
        string replacedByHash,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        Collection.UpdateOneAsync(
            Filter.Eq(t => t.Id, id),
            Update.Set(t => t.RevokedAt, now)
                .Set(t => t.ReplacedBy, replacedByHash)
                .Set(t => t.UpdatedAt, now),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Signout. A hash that matches nothing updates zero rows and is NOT an error —
    /// the route answers 204 either way.
    ///
    /// <para>
    /// Matches on the hash ALONE: no owner check and no not-yet-revoked filter,
    /// exactly as <c>revokeRefreshToken</c> does. Adding an ownership check here
    /// would be a security improvement and a parity break; it is logged separately
    /// rather than fixed in the port.
    /// </para>
    /// </summary>
    public Task RevokeByHashAsync(string tokenHash, DateTime now, CancellationToken cancellationToken = default) =>
        Collection.UpdateOneAsync(
            Filter.Eq(t => t.TokenHash, tokenHash),
            Update.Set(t => t.RevokedAt, now).Set(t => t.UpdatedAt, now),
            cancellationToken: cancellationToken);

    /// <summary>
    /// DELETE /auth/sessions/{id}. Scoped to the owner, so another user's session
    /// id is indistinguishable from an unknown one.
    /// </summary>
    public async Task<bool> RevokeOwnedAsync(
        ObjectId id,
        ObjectId userId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection.UpdateOneAsync(
            Filter.And(
                Filter.Eq(t => t.Id, id),
                Filter.Eq(t => t.UserId, userId),
                NotRevoked()),
            Update.Set(t => t.RevokedAt, now).Set(t => t.UpdatedAt, now),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Node returns `modifiedCount > 0`, not matchedCount.
        return result.ModifiedCount > 0;
    }

    /// <summary>
    /// The family sweep, shared by reuse detection, reset-password and
    /// revoke-others.
    ///
    /// <para>
    /// <paramref name="exceptHash"/> is excluded BY HASH ALONE — ownership of the
    /// supplied token is deliberately not checked, mirroring the Node route.
    /// Passing someone else's token therefore revokes all of the caller's own
    /// sessions rather than erroring. That is a known Node bug, replicated on
    /// purpose and logged separately.
    /// </para>
    /// </summary>
    public Task RevokeAllAsync(
        ObjectId userId,
        DateTime now,
        string? exceptHash = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Filter.And(Filter.Eq(t => t.UserId, userId), NotRevoked());

        if (!string.IsNullOrEmpty(exceptHash))
        {
            filter = Filter.And(filter, Filter.Ne(t => t.TokenHash, exceptHash));
        }

        return Collection.UpdateManyAsync(
            filter,
            Update.Set(t => t.RevokedAt, now).Set(t => t.UpdatedAt, now),
            cancellationToken: cancellationToken);
    }

    public Task DeleteAllAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        Collection.DeleteManyAsync(Filter.Eq(t => t.UserId, userId), cancellationToken);
}
