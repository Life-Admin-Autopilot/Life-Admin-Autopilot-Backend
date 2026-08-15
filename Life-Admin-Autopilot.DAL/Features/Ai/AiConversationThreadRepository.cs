using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// Thread lifecycle — list, create, rename, delete. Message persistence for one
/// thread stays in <see cref="AiConversationRepository"/>; this owns the SET of
/// threads a user has.
///
/// <para>
/// <b>A thread is the <c>(userId, scope, scopeId)</c> document the store was
/// always keyed by.</b> Multi-thread support did not add a dimension — it stopped
/// pinning <c>scopeId</c> to null. So there is no index migration: the existing
/// unique compound index already enforces one document per thread, and every
/// message write keeps working unchanged once it is handed a real id.
/// </para>
///
/// <para>
/// <b>The one wrinkle is the thread every existing user already has</b>, whose
/// <c>scopeId</c> is null. Null is a legitimate key in the store but cannot travel
/// in a URL path segment, so <see cref="ListAsync"/> adopts it into a real id the
/// first time it sees one. Adoption renames the key on the SAME document, so the
/// transcript rides along rather than being stranded behind a new empty thread.
/// </para>
///
/// <para>
/// Every write stamps <c>UpdatedAt</c> by hand — see the Mongoose timestamp note
/// on <see cref="AiConversationRepository"/>; the .NET driver adds nothing.
/// </para>
/// </summary>
public sealed class AiConversationThreadRepository
{
    /// <summary>Titles are one line in a narrow drawer; longer is truncated on the way in.</summary>
    public const int TitleMaxLength = 80;

    private readonly IMongoCollection<AiConversationDocument> _conversations;

    public AiConversationThreadRepository(IMongoDatabase database)
    {
        _conversations = database.GetCollection<AiConversationDocument>(MongoCollections.AiConversations);
    }

    /// <summary>Most recently active first — the order the history drawer renders.</summary>
    public async Task<IReadOnlyList<AiConversationDocument>> ListAsync(
        ObjectId userId,
        string scope,
        CancellationToken cancellationToken = default)
    {
        var documents = await _conversations
            .Find(OwnerFilter(userId, scope))
            .SortByDescending(c => c.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var document in documents.Where(d => d.ScopeId is null))
        {
            document.ScopeId = await AdoptAsync(userId, scope, cancellationToken).ConfigureAwait(false);
        }

        return documents;
    }

    /// <summary>
    /// Give the legacy null-scopeId document a real id. Runs at most once per user:
    /// the filter requires the id to still be null, so a concurrent request either
    /// wins or matches nothing and re-reads the winner's id.
    /// </summary>
    private async Task<string> AdoptAsync(ObjectId userId, string scope, CancellationToken cancellationToken)
    {
        var candidate = Guid.NewGuid().ToString();

        var adopted = await _conversations
            .FindOneAndUpdateAsync(
                Builders<AiConversationDocument>.Filter.And(
                    OwnerFilter(userId, scope),
                    Builders<AiConversationDocument>.Filter.Eq(c => c.ScopeId, null)),
                Builders<AiConversationDocument>.Update.Set(c => c.ScopeId, candidate),
                new FindOneAndUpdateOptions<AiConversationDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken)
            .ConfigureAwait(false);

        return adopted?.ScopeId ?? candidate;
    }

    public async Task<AiConversationDocument> CreateAsync(
        ObjectId userId,
        string scope,
        string? title,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;
        var document = new AiConversationDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Scope = scope,
            ScopeId = Guid.NewGuid().ToString(),
            Title = string.IsNullOrWhiteSpace(title) ? null : Summarize(title),
            Messages = new List<AiConversationMessageDocument>(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _conversations.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<bool> ExistsAsync(
        ObjectId userId,
        string scope,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var count = await _conversations
            .CountDocumentsAsync(
                ThreadFilter(userId, scope, conversationId),
                new CountOptions { Limit = 1 },
                cancellationToken)
            .ConfigureAwait(false);

        return count > 0;
    }

    public async Task<bool> RenameAsync(
        ObjectId userId,
        string scope,
        string conversationId,
        string title,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _conversations
            .UpdateOneAsync(
                ThreadFilter(userId, scope, conversationId),
                Builders<AiConversationDocument>.Update
                    .Set(c => c.Title, Summarize(title))
                    .Set(c => c.UpdatedAt, at ?? DateTime.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteAsync(
        ObjectId userId,
        string scope,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _conversations
            .DeleteOneAsync(ThreadFilter(userId, scope, conversationId), cancellationToken)
            .ConfigureAwait(false);

        return result.DeletedCount > 0;
    }

    /// <summary>
    /// Name a thread from the sentence that opened it.
    ///
    /// <para>
    /// Deliberately NOT a model call: a title is a label, and spending a Langflow
    /// round — and a slice of the user's daily quota — to summarise a sentence the
    /// user can already see would be paying real money for nothing. The filter
    /// requires the title to still be unset, so later turns leave it alone and a
    /// user's own rename is never clobbered.
    /// </para>
    /// </summary>
    public async Task TitleFromFirstMessageAsync(
        ObjectId userId,
        string scope,
        string? conversationId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var title = Summarize(text);
        if (title.Length == 0)
        {
            return;
        }

        await _conversations
            .UpdateOneAsync(
                Builders<AiConversationDocument>.Filter.And(
                    ThreadFilter(userId, scope, conversationId),
                    Builders<AiConversationDocument>.Filter.Or(
                        Builders<AiConversationDocument>.Filter.Eq(c => c.Title, null),
                        Builders<AiConversationDocument>.Filter.Exists(c => c.Title, false))),
                Builders<AiConversationDocument>.Update.Set(c => c.Title, title),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Collapse to one line, truncated on a word boundary where there is one.</summary>
    public static string Summarize(string? text, int max = TitleMaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max)
        {
            return flat;
        }

        var cut = flat[..(max - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        var kept = lastSpace > max * 0.6 ? cut[..lastSpace] : cut;
        return kept + '…';
    }

    private static FilterDefinition<AiConversationDocument> OwnerFilter(ObjectId userId, string scope) =>
        Builders<AiConversationDocument>.Filter.And(
            Builders<AiConversationDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<AiConversationDocument>.Filter.Eq(c => c.Scope, scope));

    private static FilterDefinition<AiConversationDocument> ThreadFilter(
        ObjectId userId,
        string scope,
        string? conversationId) =>
        Builders<AiConversationDocument>.Filter.And(
            OwnerFilter(userId, scope),
            Builders<AiConversationDocument>.Filter.Eq(c => c.ScopeId, conversationId));
}
