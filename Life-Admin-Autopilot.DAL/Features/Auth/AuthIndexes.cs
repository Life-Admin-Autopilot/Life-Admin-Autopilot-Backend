using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

/// <summary>
/// Indexes for the two collections this slice owns. Mongoose builds these from
/// the schema declarations in <c>models/RefreshToken.ts</c> and
/// <c>models/VerificationToken.ts</c>; the .NET driver builds nothing, so they
/// are declared here.
///
/// <para>
/// The <c>tokenHash</c> uniqueness is not an optimisation. Every lookup in this
/// slice is by hash, and a duplicate would make redemption non-deterministic.
/// The TTL indexes mirror Mongo's own reaper — every code path still checks
/// <c>expiresAt</c> explicitly, because the reaper only runs about once a minute.
/// </para>
/// </summary>
public sealed class AuthIndexes : IMongoIndexProvider
{
    public string Name => "auth";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        await UniqueAsync(
            database,
            MongoCollections.RefreshTokens,
            new BsonDocument { ["tokenHash"] = 1 },
            cancellationToken).ConfigureAwait(false);

        // The sessions/list query and the family-revoke sweep.
        await PlainAsync(
            database,
            MongoCollections.RefreshTokens,
            new BsonDocument { ["userId"] = 1, ["revokedAt"] = 1 },
            cancellationToken).ConfigureAwait(false);

        await ExpiringAsync(
            database,
            MongoCollections.RefreshTokens,
            cancellationToken).ConfigureAwait(false);

        await UniqueAsync(
            database,
            MongoCollections.VerificationTokens,
            new BsonDocument { ["tokenHash"] = 1 },
            cancellationToken).ConfigureAwait(false);

        // issueCode's "delete the previous unconsumed code" sweep.
        await PlainAsync(
            database,
            MongoCollections.VerificationTokens,
            new BsonDocument { ["userId"] = 1, ["purpose"] = 1, ["consumedAt"] = 1 },
            cancellationToken).ConfigureAwait(false);

        await ExpiringAsync(
            database,
            MongoCollections.VerificationTokens,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task UniqueAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument keys,
        CancellationToken cancellationToken) =>
        CreateAsync(database, collection, keys, new CreateIndexOptions<BsonDocument> { Unique = true }, cancellationToken);

    private static Task PlainAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument keys,
        CancellationToken cancellationToken) =>
        CreateAsync(database, collection, keys, new CreateIndexOptions<BsonDocument>(), cancellationToken);

    private static Task ExpiringAsync(
        IMongoDatabase database,
        string collection,
        CancellationToken cancellationToken) =>
        CreateAsync(
            database,
            collection,
            new BsonDocument { ["expiresAt"] = 1 },
            new CreateIndexOptions<BsonDocument> { ExpireAfter = TimeSpan.Zero },
            cancellationToken);

    private static async Task CreateAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument keys,
        CreateIndexOptions<BsonDocument> options,
        CancellationToken cancellationToken)
    {
        var model = new CreateIndexModel<BsonDocument>(new BsonDocumentIndexKeysDefinition<BsonDocument>(keys), options);
        await database
            .GetCollection<BsonDocument>(collection)
            .Indexes.CreateOneAsync(model, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
