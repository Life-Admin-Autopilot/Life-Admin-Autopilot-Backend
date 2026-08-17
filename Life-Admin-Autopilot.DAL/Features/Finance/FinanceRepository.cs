using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Finance;

/// <summary>
/// How many priced rows one summary will read. Well above any realistic personal
/// admin history, and present so a pathological account degrades by dropping the
/// oldest rows rather than by timing out the request.
/// </summary>
public static class FinanceLimits
{
    public const int MaxRows = 5_000;
}

public interface IFinanceRepository
{
    /// <summary>Matters carrying an amount. Soft-deleted rows excluded.</summary>
    Task<IReadOnlyList<TaskDocument>> ListPricedMattersAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default);

    /// <summary>Scans carrying their own amount.</summary>
    Task<IReadOnlyList<ScannedDocumentDocument>> ListPricedDocumentsAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many scans exist at all, priced or not. The summary reports this so it
    /// can say what it could NOT see — a total presented as "your spending" when
    /// it covers 12 of 40 documents is a number that lies by omission.
    /// </summary>
    Task<long> CountDocumentsAsync(ObjectId userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads for the financial summary. Deliberately only reads — nothing in this
/// slice writes, so a bug here can misreport but cannot corrupt.
///
/// <para>
/// Both list queries filter on <c>amount</c> EXISTING rather than pulling
/// everything and filtering in memory. Priced rows are a small minority of a
/// real account (most matters never carry a figure), so the filter is what keeps
/// the in-memory aggregation that follows cheap enough to be worth its clarity.
/// </para>
/// </summary>
public sealed class FinanceRepository : IFinanceRepository
{
    private readonly IMongoDatabase _database;

    public FinanceRepository(IMongoDatabase database) => _database = database;

    public async Task<IReadOnlyList<TaskDocument>> ListPricedMattersAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        var matters = _database.GetCollection<TaskDocument>(MongoCollections.Tasks);

        // `$exists` on the embedded document, matching the notDeleted() rationale:
        // Mongoose omits unset optional fields entirely, so this both means what it
        // says and can use an index.
        var filter = Builders<TaskDocument>.Filter.And(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Filter.Exists(t => t.DeletedAt, false),
            Builders<TaskDocument>.Filter.Exists(t => t.Amount, true));

        return await matters
            .Find(filter)
            .SortByDescending(t => t.CreatedAt)
            .Limit(FinanceLimits.MaxRows)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScannedDocumentDocument>> ListPricedDocumentsAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        var scans = _database.GetCollection<ScannedDocumentDocument>(MongoCollections.ScannedDocuments);

        var filter = Builders<ScannedDocumentDocument>.Filter.And(
            Builders<ScannedDocumentDocument>.Filter.Eq(d => d.UserId, userId),
            Builders<ScannedDocumentDocument>.Filter.Exists(d => d.Amount, true));

        return await scans
            .Find(filter)
            .SortByDescending(d => d.CreatedAt)
            .Limit(FinanceLimits.MaxRows)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> CountDocumentsAsync(ObjectId userId, CancellationToken cancellationToken = default)
    {
        var scans = _database.GetCollection<ScannedDocumentDocument>(MongoCollections.ScannedDocuments);

        return await scans
            .CountDocumentsAsync(
                Builders<ScannedDocumentDocument>.Filter.Eq(d => d.UserId, userId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Two partial indexes over the rows that actually carry a figure.
///
/// <para>
/// Partial rather than plain: an index over every matter's absent
/// <c>amount</c> would be almost entirely null entries, paying write cost on
/// every matter created to speed up a query that only ever wants the few percent
/// that are priced.
/// </para>
/// </summary>
public sealed class FinanceIndexes : IMongoIndexProvider
{
    public string Name => "finance";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var matters = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
        await matters.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { ["userId"] = 1, ["completedAt"] = -1 },
                    new CreateIndexOptions<BsonDocument>
                    {
                        PartialFilterExpression = new BsonDocument("amount", new BsonDocument("$exists", true)),
                    }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var scans = database.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments);
        await scans.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { ["userId"] = 1, ["createdAt"] = -1 },
                    new CreateIndexOptions<BsonDocument>
                    {
                        PartialFilterExpression = new BsonDocument("amount", new BsonDocument("$exists", true)),
                    }),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
