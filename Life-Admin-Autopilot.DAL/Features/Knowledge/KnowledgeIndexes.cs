using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Knowledge;

/// <summary>
/// The ordinary B-tree indexes for <c>contentChunks</c>.
///
/// <para>
/// <b>The vector index is NOT created here, and cannot be.</b> An Atlas Search index
/// is created through the Atlas Admin API or the Atlas UI — not through
/// <c>createIndexes</c>, which is all a driver can issue. Declaring it here would
/// fail on every boot. It has to be created once on the cluster, named
/// <see cref="ContentChunkVocabulary.VectorIndexName"/>, with
/// <c>numDimensions: 768</c>, <c>similarity: "cosine"</c>, and <c>userId</c>
/// declared as a filter field so the owner scoping inside $vectorSearch works.
/// See docs/RAG.md.
/// </para>
/// </summary>
public sealed class KnowledgeIndexes : IMongoIndexProvider
{
    public string Name => "contentchunks";

    public async Task EnsureAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var chunks = database.GetCollection<ContentChunkDocument>(ContentChunkVocabulary.Collection);

        // Every write replaces one source's chunks, and every erasure sweeps by user;
        // both drive off this compound key.
        var bySource = new CreateIndexModel<ContentChunkDocument>(
            Builders<ContentChunkDocument>.IndexKeys
                .Ascending(c => c.UserId)
                .Ascending(c => c.SourceType)
                .Ascending(c => c.SourceId)
                .Ascending(c => c.ChunkIndex),
            new CreateIndexOptions { Name = "userId_source_chunk" });

        await chunks.Indexes.CreateOneAsync(bySource, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
