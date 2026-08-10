using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.GoogleIntegration;

/// <summary>
/// Every read and write the Google slice performs against <c>integrations</c>.
/// </summary>
public interface IIntegrationRepository
{
    /// <summary>Port of <c>findGoogleIntegration</c> — <c>{ userId, provider: 'google' }</c>.</summary>
    Task<IntegrationDocument?> FindGoogleAsync(ObjectId userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>findOneAndUpdate({userId, provider}, {$set: …}, {new, upsert, setDefaultsOnInsert})</c>.
    /// Upserts rather than inserts: reconnecting must REPLACE the row, because a
    /// second row would strand a refresh token that nothing ever revokes.
    /// </summary>
    Task<IntegrationDocument> UpsertGoogleAsync(
        IntegrationDocument replacement,
        CancellationToken cancellationToken = default);

    /// <summary>Mongoose <c>doc.save()</c> for a row we already hold.</summary>
    Task SaveAsync(IntegrationDocument integration, CancellationToken cancellationToken = default);

    /// <summary>Mongoose <c>doc.deleteOne()</c>.</summary>
    Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The background poller's candidate set: active Google rows, oldest calendar
    /// sync first. A connection needing re-auth is terminal until the user acts, so
    /// it is filtered out here rather than failing every tick forever.
    /// </summary>
    Task<List<IntegrationDocument>> FindPollCandidatesAsync(int limit, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IIntegrationRepository"/>
public sealed class IntegrationRepository : MongoRepositoryBase<IntegrationDocument>, IIntegrationRepository
{
    public IntegrationRepository(IMongoDatabase database)
        : base(database, MongoCollections.Integrations)
    {
    }

    private static FilterDefinition<IntegrationDocument> GoogleFor(ObjectId userId) =>
        Filter.And(UserScoped(userId), Filter.Eq(i => i.Provider, IntegrationVocabulary.Google));

    public Task<IntegrationDocument?> FindGoogleAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        Collection.Find(GoogleFor(userId)).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IntegrationDocument> UpsertGoogleAsync(
        IntegrationDocument replacement,
        CancellationToken cancellationToken = default)
    {
        // Mongoose's `timestamps: true` stamps both on insert and bumps updatedAt on
        // every write. The driver does neither, so the caller sets them and this
        // method only decides insert-vs-update.
        var updated = await Collection
            .FindOneAndReplaceAsync<IntegrationDocument>(
                GoogleFor(replacement.UserId),
                replacement,
                new FindOneAndReplaceOptions<IntegrationDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return updated;
    }

    public Task SaveAsync(IntegrationDocument integration, CancellationToken cancellationToken = default) =>
        Collection.ReplaceOneAsync(
            Filter.Eq(i => i.Id, integration.Id),
            integration,
            new ReplaceOptions(),
            cancellationToken);

    public Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        Collection.DeleteOneAsync(Filter.Eq(i => i.Id, id), cancellationToken);

    public Task<List<IntegrationDocument>> FindPollCandidatesAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        Collection
            .Find(Filter.And(
                Filter.Eq(i => i.Provider, IntegrationVocabulary.Google),
                Filter.Eq(i => i.Status, IntegrationVocabulary.StatusActive)))
            .Sort(Sort.Ascending(i => i.CalendarSyncedAt))
            .Limit(limit)
            .ToListAsync(cancellationToken);
}
