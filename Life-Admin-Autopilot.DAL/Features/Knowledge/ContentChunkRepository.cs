using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Knowledge;

/// <summary>
/// Reads and writes <c>contentChunks</c>, including the <c>$vectorSearch</c> stage
/// that RAG retrieval runs on.
///
/// <para>
/// <b>Atlas only.</b> <c>$vectorSearch</c> is an Atlas-managed stage; a local
/// <c>mongod</c> does not implement it and answers with a command error rather than
/// an empty result. <see cref="SearchAsync"/> therefore surfaces that as a typed
/// failure instead of letting a confusing driver exception escape — see
/// <see cref="VectorSearchUnavailableException"/>.
/// </para>
/// </summary>
public sealed class ContentChunkRepository
{
    private readonly IMongoCollection<ContentChunkDocument> _chunks;
    private readonly ILogger<ContentChunkRepository> _logger;

    public ContentChunkRepository(IMongoDatabase database, ILogger<ContentChunkRepository> logger)
    {
        _chunks = database.GetCollection<ContentChunkDocument>(ContentChunkVocabulary.Collection);
        _logger = logger;
    }

    /// <summary>
    /// Replace every chunk for one source.
    ///
    /// <para>
    /// Delete-then-insert rather than a per-chunk upsert: an edit can change the
    /// chunk COUNT, and upserting by <c>(sourceId, chunkIndex)</c> would leave the
    /// tail of a now-shorter document behind as orphans that still match searches.
    /// </para>
    /// </summary>
    public async Task ReplaceForSourceAsync(
        ObjectId userId,
        string sourceType,
        ObjectId sourceId,
        IReadOnlyList<ContentChunkDocument> chunks,
        CancellationToken cancellationToken = default)
    {
        await _chunks
            .DeleteManyAsync(SourceFilter(userId, sourceType, sourceId), cancellationToken)
            .ConfigureAwait(false);

        if (chunks.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var chunk in chunks)
        {
            chunk.UserId = userId;
            chunk.SourceType = sourceType;
            chunk.SourceId = sourceId;
            chunk.CreatedAt = now;
            chunk.UpdatedAt = now;
        }

        await _chunks.InsertManyAsync(chunks, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteForSourceAsync(
        ObjectId userId,
        string sourceType,
        ObjectId sourceId,
        CancellationToken cancellationToken = default) =>
        _chunks.DeleteManyAsync(SourceFilter(userId, sourceType, sourceId), cancellationToken);

    /// <summary>
    /// Top-k nearest chunks belonging to <paramref name="userId"/>.
    ///
    /// <para>
    /// The owner filter is inside the <c>$vectorSearch</c> stage, not a <c>$match</c>
    /// after it. That is the difference between "search this user's corpus" and
    /// "search everyone's corpus, then hide the leaks": a post-filter would let one
    /// user's documents consume the k slots and return nothing, and any slip in the
    /// pipeline order becomes a cross-tenant read. Atlas requires the field be
    /// declared as a <c>filter</c> in the index for this to work.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ContentChunkMatch>> SearchAsync(
        ObjectId userId,
        float[] queryEmbedding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // numCandidates is the breadth of the approximate search; Atlas guidance is
        // ~10-20x the limit, floored so a tiny k still explores enough of the graph.
        var numCandidates = Math.Max(100, limit * 15);

        // The element names, NOT the C# property names. MongoKernelConventions
        // registers CamelCaseElementNameConvention globally, so these are stored as
        // `embedding` / `userId`. A `$vectorSearch` naming the PascalCase property
        // matches nothing and reports no error — it just returns zero rows forever.
        var vectorSearch = new BsonDocument("$vectorSearch", new BsonDocument
        {
            { "index", ContentChunkVocabulary.VectorIndexName },
            { "path", ContentChunkVocabulary.EmbeddingField },
            { "queryVector", new BsonArray(queryEmbedding.Select(v => (double)v)) },
            { "numCandidates", numCandidates },
            { "limit", limit },
            { "filter", new BsonDocument(ContentChunkVocabulary.UserIdField, userId) },
        });

        // vectorSearchScore is only available in the stage IMMEDIATELY after the
        // search; reading it later returns null.
        var project = new BsonDocument("$addFields",
            new BsonDocument("score", new BsonDocument("$meta", "vectorSearchScore")));

        try
        {
            var results = await _chunks
                .Aggregate<BsonDocument>(
                    new[] { vectorSearch, project },
                    cancellationToken: cancellationToken)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return results.Select(Map).ToList();
        }
        catch (MongoCommandException ex) when (IsUnknownStage(ex))
        {
            // Not fatal. $vectorSearch is an Atlas index doing approximate nearest
            // neighbour over the whole collection; with the corpus already narrowed
            // to ONE user, an exact scan is both feasible and strictly more accurate.
            // A user with a few thousand chunks is a few MB and single-digit
            // milliseconds of arithmetic — far cheaper than requiring Atlas to run
            // the feature at all. Atlas is still used when present; this is the
            // fallback, not the plan.
            _logger.LogWarning(
                "knowledge:vector-search-unavailable — falling back to an exact in-process "
                + "scan for user {UserId}. Create the Atlas index '{Index}' to use $vectorSearch.",
                userId,
                ContentChunkVocabulary.VectorIndexName);

            return await ScanAsync(userId, queryEmbedding, limit, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Exact top-k by cosine similarity, computed here rather than in the database.
    ///
    /// <para>
    /// Only this user's chunks are loaded, which is the same scoping the Atlas path
    /// enforces inside the stage — the owner filter is not an optimisation that can
    /// be dropped.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ContentChunkMatch>> ScanAsync(
        ObjectId userId,
        float[] queryEmbedding,
        int limit,
        CancellationToken cancellationToken)
    {
        var mine = await _chunks
            .Find(Builders<ContentChunkDocument>.Filter.Eq(c => c.UserId, userId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return mine
            .Select(c => new ContentChunkMatch(c, Cosine(queryEmbedding, c.Embedding)))
            .OrderByDescending(m => m.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Cosine similarity. Both sides are normalised by the embedding provider, so
    /// this is really a dot product — but the magnitudes are divided out anyway so a
    /// vector written before normalisation existed still ranks correctly.
    /// </summary>
    internal static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0d;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= double.Epsilon ? 0d : dot / denom;
    }

    /// <summary>
    /// A local <c>mongod</c> rejects the stage by name. Matched on the message rather
    /// than a code because the driver surfaces it as a generic command failure.
    /// </summary>
    private static bool IsUnknownStage(MongoCommandException ex) =>
        ex.Message.Contains("$vectorSearch", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Unrecognized pipeline stage", StringComparison.OrdinalIgnoreCase);

    private static ContentChunkMatch Map(BsonDocument raw)
    {
        var score = raw.TryGetValue("score", out var s) && s.IsNumeric ? s.ToDouble() : 0d;
        raw.Remove("score");
        var chunk = MongoDB.Bson.Serialization.BsonSerializer
            .Deserialize<ContentChunkDocument>(raw);
        return new ContentChunkMatch(chunk, score);
    }

    private static FilterDefinition<ContentChunkDocument> SourceFilter(
        ObjectId userId,
        string sourceType,
        ObjectId sourceId) =>
        Builders<ContentChunkDocument>.Filter.And(
            Builders<ContentChunkDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<ContentChunkDocument>.Filter.Eq(c => c.SourceType, sourceType),
            Builders<ContentChunkDocument>.Filter.Eq(c => c.SourceId, sourceId));

    internal IMongoCollection<ContentChunkDocument> Collection => _chunks;
}

/// <summary>Thrown when the cluster cannot run <c>$vectorSearch</c> (i.e. it is not Atlas).</summary>
public sealed class VectorSearchUnavailableException : Exception
{
    public VectorSearchUnavailableException(Exception inner)
        : base(
            "Vector search requires a MongoDB Atlas cluster with a vector index named "
            + $"'{ContentChunkVocabulary.VectorIndexName}'. The configured connection does not support $vectorSearch.",
            inner)
    {
    }
}

/// <summary>Account deletion cascade — this slice owns <c>contentChunks</c>.</summary>
public sealed class ContentChunkEraser : IUserDataEraser
{
    private readonly ContentChunkRepository _chunks;

    public ContentChunkEraser(ContentChunkRepository chunks) => _chunks = chunks;

    public string Name => "content-chunks";

    public Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default) =>
        _chunks.Collection.DeleteManyAsync(
            Builders<ContentChunkDocument>.Filter.Eq(c => c.UserId, context.UserId),
            cancellationToken);
}
