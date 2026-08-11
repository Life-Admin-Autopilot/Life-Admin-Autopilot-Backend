using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.Ai;

/// <summary>
/// The closed vocabularies of <c>server/src/models/AiConversation.ts</c>.
/// </summary>
public static class AiConversationVocabulary
{
    /// <summary>
    /// <c>AI_CONVERSATION_MAX_TURNS</c>. Every <c>$push</c> carries
    /// <c>$slice: -MAX</c>, so the document stays bounded with no background sweep.
    /// </summary>
    public const int MaxTurns = 50;

    /// <summary>The only scope v1 implements. The route hard-codes it.</summary>
    public const string PersonalScope = "personal";

    /// <summary>The one tool-call status that may be confirmed.</summary>
    public const string PendingConfirmation = "pending_confirmation";

    public static readonly IReadOnlyList<string> Scopes = new[] { PersonalScope };

    public static readonly IReadOnlyList<string> ToolCallStatuses = new[]
    {
        PendingConfirmation, "executed", "failed", "declined",
    };

    public static readonly IReadOnlyList<string> SourceKinds = new[] { "task", "voice" };

    public static readonly IReadOnlyList<string> Roles = new[] { "user", "assistant" };
}

/// <summary>
/// A grounding citation. <c>_id: false</c> in the Mongoose sub-schema, so there is
/// no id to rename on the way out.
/// </summary>
public sealed class AiConversationSourceDocument
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The cited task or voice-note id — a PLAIN <c>id</c> field, not the document
    /// key. It is deliberately NOT named <c>Id</c>: the driver's default
    /// <c>NamedIdMemberConvention</c> would claim a member called <c>Id</c> as the
    /// document key and map it to <c>_id</c>, which this sub-schema does not have
    /// (<c>_id: false</c>). The symptom is silent — every source reads back with an
    /// empty id and nothing errors.
    /// </summary>
    [BsonElement("id")]
    public string SourceId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// One model-emitted tool invocation.
///
/// <para>
/// <b><c>Result</c> and <c>Error</c> are declared <c>default: null</c> in Mongoose,
/// not <c>undefined</c>.</b> Mongoose applies schema defaults on hydration as well
/// as on create, so both keys are ALWAYS present in the serialized form — as
/// literal <c>null</c> when nothing has been written. The DTO reproduces that with
/// <c>JsonIgnoreCondition.Never</c>; here they are simply nullable.
/// </para>
///
/// <para><c>ModelCallId</c> has no default, so an absent value stays absent.</para>
/// </summary>
public sealed class AiConversationToolCallDocument
{
    public string CallId { get; set; } = string.Empty;

    /// <summary>Gemini's own id, echoed back so multi-turn function pairs match.</summary>
    public string? ModelCallId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <c>Schema.Types.Mixed</c>, required. In practice always an object — the tool
    /// schemas are zod objects — but typed as <see cref="BsonValue"/> rather than
    /// <c>BsonDocument</c> to match what the storage layer actually permits, the way
    /// <see cref="Result"/> does. A narrower type would make one oddly-shaped stored
    /// value throw during deserialization of the WHOLE conversation, turning every
    /// read of that user's history into a 500.
    /// </summary>
    public BsonValue Args { get; set; } = new BsonDocument();

    public string Status { get; set; } = AiConversationVocabulary.PendingConfirmation;

    /// <summary><c>Schema.Types.Mixed</c>, nullable. May be a document or BSON null.</summary>
    [BsonIgnoreIfNull]
    public BsonValue? Result { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One turn. <c>_id: false</c>, so messages carry no ids.
///
/// <para>
/// <c>sources</c> and <c>toolCalls</c> are declared <c>default: undefined</c> —
/// Mongoose stores nothing when they are empty, and the ROUTE (not the model)
/// coerces the absent value to <c>[]</c> on the way out.
/// </para>
/// </summary>
public sealed class AiConversationMessageDocument
{
    public string Role { get; set; } = string.Empty;

    /// <summary>May be the empty string — a tool-only assistant turn has no prose.</summary>
    public string Text { get; set; } = string.Empty;

    public List<AiConversationSourceDocument>? Sources { get; set; }

    public List<AiConversationToolCallDocument>? ToolCalls { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Port of <c>server/src/models/AiConversation.ts</c> — one document per
/// <c>(userId, scope, scopeId)</c>, uniquely indexed on that triple.
/// </summary>
public sealed class AiConversationDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    public string Scope { get; set; } = AiConversationVocabulary.PersonalScope;

    /// <summary>
    /// Always <c>null</c> in v1, and stored as an explicit null rather than being
    /// omitted — the unique index spans it, so the key must exist for every
    /// document to occupy the same key space.
    /// </summary>
    [BsonIgnoreIfNull(false)]
    public string? ScopeId { get; set; }

    public List<AiConversationMessageDocument> Messages { get; set; } = new();

    /// <summary>
    /// <b>The conversation GENERATION — not in the Mongoose schema, and the one field
    /// here Node does not write.</b> See <c>docs/DIVERGENCES.md</c>.
    ///
    /// <para>
    /// An external agent (Langflow) keeps its OWN conversation memory, keyed on the
    /// <c>session_id</c> we send it. If that key is derived from the user alone it
    /// never changes, so <c>POST /ai/conversation/reset</c> — which only empties the
    /// messages below — clears the history the user can see and leaves the history
    /// the agent answers from. The reset button then lies.
    /// </para>
    ///
    /// <para>
    /// So reset rotates this to a fresh id, and the session key we send is derived
    /// from it (<see cref="SessionGeneration"/>). <b>Absent until the first reset</b>:
    /// a conversation that has never been reset generates from its own <c>_id</c>,
    /// which is why no insert path has to remember to stamp this and why the stored
    /// document stays byte-identical to Node's until a reset actually happens.
    /// </para>
    /// </summary>
    [BsonIgnoreIfNull]
    public ObjectId? SessionKey { get; set; }

    /// <summary>
    /// What an external agent's per-session memory is keyed on: the rotating
    /// <see cref="SessionKey"/> once a reset has minted one, and the document's own
    /// id before that. Never null, always unique to one user's one conversation
    /// generation. Computed, never stored.
    /// </summary>
    [BsonIgnore]
    public ObjectId SessionGeneration => SessionKey ?? Id;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Mongoose's version key. Never serialized to a client — modelled only so the
    /// documents this slice writes are byte-identical to the ones the Node server
    /// writes into the same parity database. Verified live: both
    /// <c>AiConversation.create()</c> and the upsert path stamp <c>__v: 0</c>.
    /// </summary>
    [BsonElement("__v")]
    public int Version { get; set; }
}
