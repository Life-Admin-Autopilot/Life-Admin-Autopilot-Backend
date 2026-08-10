using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.GoogleIntegration;

/// <summary>
/// The indexes declared on <c>IntegrationSchema</c>.
///
/// <para>
/// <c>{userId, provider}</c> is <b>unique</b>, and that is a correctness
/// requirement rather than an optimisation: the connection upsert relies on there
/// being at most one row per provider per user. A second row would strand a refresh
/// token that nothing ever revokes.
/// </para>
/// </summary>
public sealed class IntegrationIndexes : IMongoIndexProvider
{
    public string Name => "integrations";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var integrations = database.GetCollection<BsonDocument>(MongoCollections.Integrations);

        await integrations.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { ["userId"] = 1, ["provider"] = 1 },
                    new CreateIndexOptions { Unique = true }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Declared `index: true` on the two fields the poller filters and sorts on.
        await integrations.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(new BsonDocument { ["userId"] = 1 }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await integrations.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(new BsonDocument { ["status"] = 1 }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Drops the connection when the account is deleted. Registered at
/// <see cref="UserErasureOrder.Dependents"/>, per KERNEL.md §8.6 and the §14
/// checklist ("an <c>IUserDataEraser</c> for every collection the slice owns").
///
/// <para>
/// <b>Deliberate divergence from Node, reported to the coordinator.</b>
/// <c>routes/me.ts</c> deletes twelve collections by hand and <c>integrations</c>
/// is NOT one of them, so Node leaves an orphaned row — and therefore an encrypted
/// refresh token that nothing will ever revoke — behind a deleted account. The
/// kernel's eraser registry exists precisely to replace that hand-maintained list,
/// so this slice registers rather than reproducing the omission.
/// </para>
///
/// <para>
/// The one place it is observable: after <c>DELETE /me</c>, with an access token
/// that is still cryptographically valid, <c>GET /integrations/google</c> would
/// report the orphaned row on Node and <c>null</c> here. No harness row exercises
/// that sequence. Slice K owns <c>DELETE /me</c> and can decide otherwise.
/// </para>
///
/// <para>
/// Erases the LOCAL row only — it does not call Google's revoke endpoint, because
/// the cascade must stay re-runnable and a network call cannot be.
/// </para>
/// </summary>
public sealed class IntegrationEraser : MongoCollectionEraser
{
    public IntegrationEraser(IMongoDatabase database)
        : base("integrations", MongoCollections.Integrations) => UseDatabase(database);
}
