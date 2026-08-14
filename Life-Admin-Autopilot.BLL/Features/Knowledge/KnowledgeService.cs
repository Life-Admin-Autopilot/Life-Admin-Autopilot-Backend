using Life_Admin_Autopilot.DAL.Features.Knowledge;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>
/// The two halves of RAG: put text in (<see cref="IngestAsync"/>) and get the
/// relevant parts back out (<see cref="SearchAsync"/>).
///
/// <para>
/// <b>Ingest is best-effort by design.</b> It is called from the task write path,
/// and a user creating a matter must not see a 500 because an embedding provider
/// was slow. A missing chunk degrades retrieval; a failed write loses their data.
/// So failures are logged and swallowed here — never at the retrieval end, where a
/// silent empty result would look like "you have nothing on that".
/// </para>
/// </summary>
public sealed class KnowledgeService
{
    /// <summary>
    /// Characters per chunk. Tasks are far shorter than this and stay single-chunk;
    /// the limit exists for scanned documents. Deliberately expressed in characters,
    /// not tokens: Arabic tokenises very differently from English and a token budget
    /// tuned on one silently truncates the other.
    /// </summary>
    private const int MaxChunkChars = 1200;

    /// <summary>
    /// Carried between adjacent chunks so a sentence split across a boundary is still
    /// retrievable from either side.
    /// </summary>
    private const int OverlapChars = 150;

    private readonly IEmbeddingProvider _embeddings;
    private readonly ContentChunkRepository _chunks;
    private readonly ILogger<KnowledgeService> _logger;

    public KnowledgeService(
        IEmbeddingProvider embeddings,
        ContentChunkRepository chunks,
        ILogger<KnowledgeService> logger)
    {
        _embeddings = embeddings;
        _chunks = chunks;
        _logger = logger;
    }

    public bool IsConfigured => _embeddings.IsConfigured;

    /// <summary>
    /// Embed <paramref name="text"/> and replace whatever was stored for this source.
    /// Safe to call on every write — empty text just clears the source's chunks.
    /// </summary>
    public async Task IngestAsync(
        ObjectId userId,
        string sourceType,
        ObjectId sourceId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return;

        try
        {
            var pieces = Chunk(text);
            if (pieces.Count == 0)
            {
                await _chunks
                    .DeleteForSourceAsync(userId, sourceType, sourceId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var documents = new List<ContentChunkDocument>(pieces.Count);
            for (var i = 0; i < pieces.Count; i++)
            {
                var vector = await _embeddings
                    .EmbedAsync(pieces[i], isQuery: false, cancellationToken)
                    .ConfigureAwait(false);

                documents.Add(new ContentChunkDocument
                {
                    Id = ObjectId.GenerateNewId(),
                    ChunkIndex = i,
                    Text = pieces[i],
                    Embedding = vector,
                    Model = _embeddings.Model,
                });
            }

            await _chunks
                .ReplaceForSourceAsync(userId, sourceType, sourceId, documents, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "knowledge:ingested source={SourceType}/{SourceId} chunks={Count}",
                sourceType, sourceId, documents.Count);
        }
        catch (Exception ex)
        {
            // See the class remarks: never fail the caller's write.
            _logger.LogWarning(
                ex,
                "knowledge:ingest-failed source={SourceType}/{SourceId}",
                sourceType, sourceId);
        }
    }

    public Task ForgetAsync(
        ObjectId userId,
        string sourceType,
        ObjectId sourceId,
        CancellationToken cancellationToken = default) =>
        _chunks.DeleteForSourceAsync(userId, sourceType, sourceId, cancellationToken);

    /// <summary>
    /// Top-k chunks for a natural-language question, scoped to one user.
    /// Unlike ingest this propagates failures — see the class remarks.
    /// </summary>
    public async Task<IReadOnlyList<ContentChunkMatch>> SearchAsync(
        ObjectId userId,
        string question,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var vector = await _embeddings
            .EmbedAsync(question, isQuery: true, cancellationToken)
            .ConfigureAwait(false);

        return await _chunks
            .SearchAsync(userId, vector, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Split on blank lines first so a chunk boundary lands between paragraphs rather
    /// than mid-thought, packing whole paragraphs together until the budget is spent.
    /// A single paragraph longer than the budget is hard-split with overlap.
    /// </summary>
    internal static List<string> Chunk(string? text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var normalised = text.Replace("\r\n", "\n").Trim();
        var paragraphs = normalised
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var current = new System.Text.StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            chunks.Add(current.ToString().Trim());
            current.Clear();
        }

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > MaxChunkChars)
            {
                Flush();
                chunks.AddRange(HardSplit(paragraph));
                continue;
            }

            if (current.Length + paragraph.Length + 2 > MaxChunkChars) Flush();

            if (current.Length > 0) current.Append("\n\n");
            current.Append(paragraph);
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> HardSplit(string paragraph)
    {
        var start = 0;
        while (start < paragraph.Length)
        {
            var length = Math.Min(MaxChunkChars, paragraph.Length - start);
            yield return paragraph.Substring(start, length).Trim();
            if (start + length >= paragraph.Length) yield break;
            // Step back by the overlap so the next window re-reads the tail.
            start += Math.Max(1, length - OverlapChars);
        }
    }
}
