using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.Knowledge;

/// <summary>
/// One embedded slice of the user's own corpus — the <c>contentChunks</c> collection
/// from the team's MongoDB schema (SRS §6), and the store behind RAG retrieval
/// (stories #15/#82).
///
/// <para>
/// <b>Source-agnostic on purpose.</b> The architecture diagram is explicit that
/// "every task is embedded, not just documents", so a voice- or text-created matter
/// is retrievable too. <see cref="SourceType"/> plus <see cref="SourceId"/> is the
/// polymorphic pointer back to the row it came from.
/// </para>
/// </summary>
public static class ContentChunkVocabulary
{
    /// <summary>
    /// Declared here rather than in <c>MongoCollections</c>, which documents itself as
    /// a merge-conflict magnet and tells slices to own their own name.
    /// </summary>
    public const string Collection = "contentchunks";

    public const string TaskSource = "task";
    public const string DocumentSource = "document";

    /// <summary>
    /// Dimensionality requested from the embedding model, and therefore the
    /// <c>numDimensions</c> the Atlas vector index MUST declare. Changing this
    /// invalidates every stored vector — the index rejects a mismatched length, so a
    /// change means a re-embed, not just a redeploy.
    /// </summary>
    public const int Dimensions = 768;

    /// <summary>The Atlas Search index name the <c>$vectorSearch</c> stage names.</summary>
    public const string VectorIndexName = "contentchunks_embedding_idx";

    /// <summary>
    /// Stored element names, which are NOT the C# property names —
    /// <c>MongoKernelConventions</c> registers <c>CamelCaseElementNameConvention</c>
    /// globally. The Atlas index definition and the <c>$vectorSearch</c> stage must
    /// both use these; a PascalCase path matches nothing and raises no error.
    /// </summary>
    public const string EmbeddingField = "embedding";

    public const string UserIdField = "userId";
}

public sealed class ContentChunkDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>
    /// The owner. <b>Every retrieval filters on this</b> — a vector search without it
    /// would happily return another user's documents, which is the one failure mode
    /// this collection cannot be allowed to have.
    /// </summary>
    public ObjectId UserId { get; set; }

    /// <summary><c>task</c> | <c>document</c>. See <see cref="ContentChunkVocabulary"/>.</summary>
    public string SourceType { get; set; } = ContentChunkVocabulary.TaskSource;

    /// <summary>Polymorphic — points at a task or a scanned document.</summary>
    public ObjectId SourceId { get; set; }

    /// <summary>
    /// Position within the source, 0-based. A short task yields exactly one chunk; a
    /// multi-page document yields many, and re-ingesting replaces the whole set for
    /// that source rather than diffing.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>The text that was embedded, kept verbatim so a hit can be shown to the user.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The embedding. <c>float</c> rather than <c>double</c> deliberately: Atlas stores
    /// these as BSON doubles either way, but a 768-wide double array is 6KB per chunk
    /// against 3KB as float, and the extra precision is meaningless for cosine ranking.
    /// </summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Model that produced <see cref="Embedding"/>. Stored so a model swap is
    /// detectable: vectors from two different models are not comparable, and mixing
    /// them silently degrades ranking rather than erroring.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>One retrieval hit: the chunk plus the similarity Atlas scored it at.</summary>
public sealed record ContentChunkMatch(ContentChunkDocument Chunk, double Score);
