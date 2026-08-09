using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

public interface IVerificationTokenRepository
{
    Task InsertAsync(VerificationTokenDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically redeems a token: matches on hash + purpose, unconsumed and
    /// unexpired, and stamps <c>consumedAt</c> in the same round trip. Returns the
    /// PRE-update document, or null when nothing matched.
    /// </summary>
    Task<VerificationTokenDocument?> ConsumeAsync(
        string tokenHash,
        string purpose,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The 6-digit-code variant. Redundantly scopes to <paramref name="userId"/> as
    /// well as the salted hash, mirroring <c>consumeCode</c>.
    /// </summary>
    Task<bool> ConsumeCodeAsync(
        ObjectId userId,
        string tokenHash,
        string purpose,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>Drops every unconsumed token of one purpose for one user.</summary>
    Task DeleteUnconsumedAsync(ObjectId userId, string purpose, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(ObjectId userId, CancellationToken cancellationToken = default);
}

public sealed class VerificationTokenRepository
    : MongoRepositoryBase<VerificationTokenDocument>, IVerificationTokenRepository
{
    public VerificationTokenRepository(IMongoDatabase database)
        : base(database, MongoCollections.VerificationTokens)
    {
    }

    public Task InsertAsync(VerificationTokenDocument document, CancellationToken cancellationToken = default) =>
        Collection.InsertOneAsync(document, cancellationToken: cancellationToken);

    /// <summary>
    /// Single-use is enforced HERE rather than by a read-then-write, so two
    /// simultaneous redemptions cannot both succeed. A token that is unknown,
    /// already consumed, expired, or minted for another purpose all return null —
    /// the caller folds every one of them into the same 400.
    /// </summary>
    public Task<VerificationTokenDocument?> ConsumeAsync(
        string tokenHash,
        string purpose,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        Collection.FindOneAndUpdateAsync(
            Filter.And(
                Filter.Eq(t => t.TokenHash, tokenHash),
                Filter.Eq(t => t.Purpose, purpose),
                Filter.Exists(t => t.ConsumedAt, false),
                Filter.Gt(t => t.ExpiresAt, now)),
            Update.Set(t => t.ConsumedAt, now).Set(t => t.UpdatedAt, now),
            new FindOneAndUpdateOptions<VerificationTokenDocument>
            {
                ReturnDocument = ReturnDocument.Before,
            },
            cancellationToken)!;

    public async Task<bool> ConsumeCodeAsync(
        ObjectId userId,
        string tokenHash,
        string purpose,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var consumed = await Collection.FindOneAndUpdateAsync(
            Filter.And(
                Filter.Eq(t => t.UserId, userId),
                Filter.Eq(t => t.Purpose, purpose),
                Filter.Eq(t => t.TokenHash, tokenHash),
                Filter.Exists(t => t.ConsumedAt, false),
                Filter.Gt(t => t.ExpiresAt, now)),
            Update.Set(t => t.ConsumedAt, now).Set(t => t.UpdatedAt, now),
            new FindOneAndUpdateOptions<VerificationTokenDocument> { ReturnDocument = ReturnDocument.Before },
            cancellationToken).ConfigureAwait(false);

        return consumed is not null;
    }

    /// <summary>
    /// Called before minting a new code, so a resend INVALIDATES the previous one
    /// instead of widening the guessing window. Exactly one live code per
    /// (user, purpose).
    /// </summary>
    public Task DeleteUnconsumedAsync(ObjectId userId, string purpose, CancellationToken cancellationToken = default) =>
        Collection.DeleteManyAsync(
            Filter.And(
                Filter.Eq(t => t.UserId, userId),
                Filter.Eq(t => t.Purpose, purpose),
                Filter.Exists(t => t.ConsumedAt, false)),
            cancellationToken);

    public Task DeleteAllAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        Collection.DeleteManyAsync(Filter.Eq(t => t.UserId, userId), cancellationToken);
}
