using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// Port of <c>server/src/modules/ai/conversationService.ts</c> — one document per
/// <c>(userId, scope, scopeId)</c>, always upserted so no caller ever branches on
/// "first time" versus "returning".
///
/// <para>
/// <b>The Mongoose timestamp trap.</b> These models declare
/// <c>{ timestamps: true }</c>, and Mongoose rewrites every update to add
/// <c>updatedAt</c> INTO the <c>$set</c> and <c>createdAt</c> (plus <c>__v: 0</c>)
/// into a <c>$setOnInsert</c>. The .NET driver does none of that. Every write below
/// therefore stamps them by hand — measured against a live reference document, not
/// inferred.
/// </para>
/// </summary>
public sealed class AiConversationRepository
{
    private const int DuplicateKeyErrorCode = 11000;

    private readonly IMongoCollection<AiConversationDocument> _conversations;

    public AiConversationRepository(IMongoDatabase database)
    {
        _conversations = database.GetCollection<AiConversationDocument>(MongoCollections.AiConversations);
    }

    /// <summary>
    /// <c>loadConversation</c> — find, or create an empty one. Never 404s.
    /// </summary>
    public async Task<AiConversationDocument> LoadAsync(
        ObjectId userId,
        string scope,
        string? scopeId,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var filter = KeyFilter(userId, scope, scopeId);

        var existing = await _conversations
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var now = at ?? DateTime.UtcNow;
        var created = new AiConversationDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Scope = scope,
            ScopeId = scopeId,
            Messages = new List<AiConversationMessageDocument>(),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };

        try
        {
            await _conversations.InsertOneAsync(created, cancellationToken: cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Code == DuplicateKeyErrorCode)
        {
            // A concurrent first read won the insert. Node lets the duplicate-key
            // error escape as a 500; re-reading is the same answer the caller would
            // have got a millisecond earlier, and the race is not client-observable.
            return await _conversations
                       .Find(filter)
                       .FirstOrDefaultAsync(cancellationToken)
                       .ConfigureAwait(false)
                   ?? created;
        }
    }

    /// <summary>
    /// <c>resetConversation</c> — <c>$set: { messages: [] }</c> with upsert, PLUS a
    /// fresh <see cref="AiConversationDocument.SessionKey"/>. The route answers with a
    /// literal empty envelope regardless, so this is purely the write.
    ///
    /// <para>
    /// <b>The rotation is the half that makes the reset honest.</b> Emptying
    /// <c>messages</c> clears the history the user can see; an external agent keyed on
    /// a session id derived from the user alone still answers from everything it was
    /// told. A new generation here means the next turn addresses a session that agent
    /// has never seen.
    /// </para>
    ///
    /// <para>
    /// Returns the generation that was just retired, or null when there was no
    /// conversation to reset — the caller uses it to tell the agent to forget that
    /// session's messages, which is a best-effort courtesy and never a precondition.
    /// </para>
    /// </summary>
    public async Task<ObjectId?> ResetAsync(
        ObjectId userId,
        string scope,
        string? scopeId,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        var update = Builders<AiConversationDocument>.Update
            .Set(c => c.Messages, new List<AiConversationMessageDocument>())
            .Set(c => c.SessionKey, ObjectId.GenerateNewId())
            .Set(c => c.UpdatedAt, now)
            .SetOnInsert(c => c.CreatedAt, now)
            .SetOnInsert(c => c.Version, 0);

        // Before-image, so the retired generation is read and replaced in ONE
        // round trip: a read-then-write would let a turn racing the reset be told
        // to forget the session it is currently streaming into.
        var previous = await _conversations
            .FindOneAndUpdateAsync(
                KeyFilter(userId, scope, scopeId),
                update,
                new FindOneAndUpdateOptions<AiConversationDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.Before,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Null on the upsert-insert path — there was nothing to retire.
        return previous?.SessionGeneration;
    }

    /// <summary>
    /// <c>appendTurn</c> — <b>every push carries <c>$slice: -MaxTurns</c></b>, which
    /// is what keeps the document bounded without a sweeper. Dropping the slice is a
    /// silent unbounded-growth bug, so it lives here and nowhere else.
    /// </summary>
    public Task AppendTurnAsync(
        ObjectId userId,
        string scope,
        string? scopeId,
        AiConversationMessageDocument message,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        var update = Builders<AiConversationDocument>.Update
            .PushEach(c => c.Messages, new[] { message }, slice: -AiConversationVocabulary.MaxTurns)
            .Set(c => c.UpdatedAt, now)
            .SetOnInsert(c => c.CreatedAt, now)
            .SetOnInsert(c => c.Version, 0);

        return _conversations.UpdateOneAsync(
            KeyFilter(userId, scope, scopeId),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>
    /// <c>recentTurns</c> — the last <paramref name="count"/> messages, capped at the
    /// document's own <c>MaxTurns</c> so a future bug cannot ask for an unbounded
    /// slice. Returns an empty list when no conversation exists yet; it does NOT
    /// upsert, unlike <see cref="LoadAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<AiConversationMessageDocument>> RecentTurnsAsync(
        ObjectId userId,
        string scope,
        int count = 10,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Max(1, Math.Min(count, AiConversationVocabulary.MaxTurns));

        var document = await _conversations
            .Find(KeyFilter(userId, scope, scopeId))
            .Project<AiConversationDocument>(
                new BsonDocument("messages", new BsonDocument("$slice", -capped)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return document?.Messages ?? new List<AiConversationMessageDocument>();
    }

    /// <summary>
    /// <c>expireStalePendingToolCalls</c> — flip every <c>pending_confirmation</c>
    /// call older than <paramref name="maxAge"/> to <c>declined</c>.
    ///
    /// <para>
    /// <b>This is what stops a durable pending record from becoming immortal.</b> The
    /// confirm route treats the stored record as the source of truth precisely so a
    /// confirmation survives a restart; without a sweep the same property would let a
    /// "Delete everything?" card from last month still be confirmable. One hour is
    /// the window Node uses, and the sweep is idempotent — a second run changes
    /// nothing, because the status is no longer pending.
    /// </para>
    ///
    /// <para>Returns the number of calls flipped.</para>
    /// </summary>
    public async Task<long> ExpireStalePendingToolCallsAsync(
        ObjectId userId,
        string scope,
        TimeSpan maxAge,
        DateTime? at = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        var cutoff = (at ?? DateTime.UtcNow) - maxAge;

        var filter = Builders<AiConversationDocument>.Filter.And(
            KeyFilter(userId, scope, scopeId),
            Builders<AiConversationDocument>.Filter.ElemMatch(
                c => c.Messages,
                Builders<AiConversationMessageDocument>.Filter.And(
                    Builders<AiConversationMessageDocument>.Filter.Lt(m => m.CreatedAt, cutoff),
                    Builders<AiConversationMessageDocument>.Filter.ElemMatch(
                        m => m.ToolCalls!,
                        Builders<AiConversationToolCallDocument>.Filter.Eq(
                            t => t.Status, AiConversationVocabulary.PendingConfirmation)))));

        var update = new BsonDocumentUpdateDefinition<AiConversationDocument>(
            new BsonDocument(
                "$set",
                new BsonDocument("messages.$[msg].toolCalls.$[call].status", "declined")));

        var options = new UpdateOptions
        {
            ArrayFilters = new ArrayFilterDefinition[]
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
                {
                    ["msg.createdAt"] = new BsonDocument("$lt", cutoff),
                    ["msg.toolCalls"] = new BsonDocument("$type", "array"),
                }),
                new BsonDocumentArrayFilterDefinition<BsonDocument>(
                    new BsonDocument("call.status", AiConversationVocabulary.PendingConfirmation)),
            },
        };

        var result = await _conversations
            .UpdateOneAsync(filter, update, options, cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }

    /// <summary>
    /// <c>findPendingToolCall</c> — the DURABLE record is the source of truth for
    /// <c>POST /ai/tools/confirm/{callId}</c>. The in-memory map Node also keeps is
    /// a cache that does not survive a restart; a confirmation that outlived a
    /// deploy used to 404 forever because of it.
    ///
    /// <para>Returns null when no message in the caller's conversation carries a
    /// tool call with this id.</para>
    /// </summary>
    public async Task<AiConversationToolCallDocument?> FindToolCallAsync(
        ObjectId userId,
        string scope,
        string callId,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<AiConversationDocument>.Filter.And(
            KeyFilter(userId, scope, scopeId),
            Builders<AiConversationDocument>.Filter.ElemMatch(
                c => c.Messages,
                Builders<AiConversationMessageDocument>.Filter.ElemMatch(
                    m => m.ToolCalls!,
                    Builders<AiConversationToolCallDocument>.Filter.Eq(t => t.CallId, callId))));

        var document = await _conversations
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return document?
            .Messages
            .Select(m => m.ToolCalls?.FirstOrDefault(c => c.CallId == callId))
            .FirstOrDefault(c => c is not null);
    }

    /// <summary>
    /// <c>resolveToolCall</c> — flip one call's status in place and write back the
    /// result or the error. <c>result</c> and <c>error</c> are only touched when the
    /// caller supplied them, mirroring Node's <c>!== undefined</c> guards.
    /// </summary>
    public async Task<long> ResolveToolCallAsync(
        ObjectId userId,
        string scope,
        string callId,
        string status,
        BsonValue? result = null,
        string? error = null,
        bool writeResult = false,
        bool writeError = false,
        DateTime? at = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        var set = new BsonDocument
        {
            ["messages.$[msg].toolCalls.$[call].status"] = status,
            ["updatedAt"] = at ?? DateTime.UtcNow,
        };

        if (writeResult)
        {
            set["messages.$[msg].toolCalls.$[call].result"] = result ?? BsonNull.Value;
        }

        if (writeError)
        {
            set["messages.$[msg].toolCalls.$[call].error"] = error is null ? BsonNull.Value : error;
        }

        var filter = Builders<AiConversationDocument>.Filter.And(
            KeyFilter(userId, scope, scopeId),
            Builders<AiConversationDocument>.Filter.ElemMatch(
                c => c.Messages,
                Builders<AiConversationMessageDocument>.Filter.ElemMatch(
                    m => m.ToolCalls!,
                    Builders<AiConversationToolCallDocument>.Filter.Eq(t => t.CallId, callId))));

        var options = new UpdateOptions
        {
            ArrayFilters = new ArrayFilterDefinition[]
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(
                    new BsonDocument("msg.toolCalls.callId", callId)),
                new BsonDocumentArrayFilterDefinition<BsonDocument>(
                    new BsonDocument("call.callId", callId)),
            },
        };

        var updated = await _conversations
            .UpdateOneAsync(
                filter,
                new BsonDocumentUpdateDefinition<AiConversationDocument>(new BsonDocument("$set", set)),
                options,
                cancellationToken)
            .ConfigureAwait(false);

        return updated.ModifiedCount;
    }

    /// <summary>
    /// <c>keyFilter</c>. <c>scopeId</c> is matched as an explicit <c>null</c>, not as
    /// "absent" — the unique index spans it and every document stores the key.
    /// </summary>
    private static FilterDefinition<AiConversationDocument> KeyFilter(
        ObjectId userId,
        string scope,
        string? scopeId) =>
        Builders<AiConversationDocument>.Filter.And(
            Builders<AiConversationDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<AiConversationDocument>.Filter.Eq(c => c.Scope, scope),
            Builders<AiConversationDocument>.Filter.Eq(c => c.ScopeId, scopeId));
}
