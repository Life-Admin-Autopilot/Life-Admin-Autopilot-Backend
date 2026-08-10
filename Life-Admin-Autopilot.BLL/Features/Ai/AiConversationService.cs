using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// The conversation half of the AI shell — everything the chat surface needs that
/// does NOT involve a model, which on the current parity target is all of it.
///
/// <para>
/// Scope is hard-coded to <c>personal</c> / <c>scopeId: null</c> at the route, so
/// this service takes neither: a second scope is a request-shape change, not a
/// parameter someone forgot to thread through.
/// </para>
/// </summary>
public sealed class AiConversationService
{
    /// <summary>
    /// The two literal 404 messages <c>POST /ai/tools/confirm/{callId}</c> can
    /// produce. <b>Same code, different text</b> — the first is "no such call", the
    /// second is "the call exists but is already resolved", and the client
    /// distinguishes them on the message. Copy-verbatim territory.
    /// </summary>
    public const string PendingCallNotFoundCode = "pending_call_not_found";

    public const string ConfirmationExpired = "This confirmation has expired.";

    public const string ConfirmationAlreadyHandled = "This confirmation has already been handled.";

    private readonly AiConversationRepository _conversations;

    public AiConversationService(AiConversationRepository conversations)
    {
        _conversations = conversations;
    }

    /// <summary>
    /// <c>GET /ai/conversation</c>. Upserts an empty document on first read, so it
    /// never 404s.
    /// </summary>
    public async Task<AiConversationResponse> LoadAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        var document = await _conversations
            .LoadAsync(userId, AiConversationVocabulary.PersonalScope, null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return document.ToResponse();
    }

    /// <summary>
    /// <c>POST /ai/conversation/reset</c>. Idempotent, and the response is the
    /// literal empty envelope rather than a re-read — Node never reloads here.
    /// </summary>
    public async Task<AiConversationResponse> ResetAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        await _conversations
            .ResetAsync(userId, AiConversationVocabulary.PersonalScope, null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return AiConversationResponse.Empty;
    }

    /// <summary>
    /// The pre-stream lookup for <c>POST /ai/tools/confirm/{callId}</c>.
    ///
    /// <para>
    /// <b>The DURABLE record is the source of truth.</b> Node also keeps an
    /// in-memory map, but it is only a cache that does not survive a restart — a
    /// "Confirm delete?" turn that outlived a deploy used to 404 forever. The
    /// cache carries exactly one field the record does not (the caller's timezone),
    /// which is why it still exists there and is not modelled here: with no model
    /// wired there is no continuation to anchor.
    /// </para>
    ///
    /// <para>
    /// <c>callId</c> is a <c>randomUUID()</c> string, NOT an ObjectId, so a
    /// malformed value takes the ordinary not-found branch and never the
    /// cast-error one.
    /// </para>
    /// </summary>
    public async Task<AiConversationToolCallDocument> RequirePendingToolCallAsync(
        ObjectId userId,
        string callId,
        CancellationToken cancellationToken = default)
    {
        var call = await _conversations
            .FindToolCallAsync(userId, AiConversationVocabulary.PersonalScope, callId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (call is null)
        {
            throw AppException.NotFound(PendingCallNotFoundCode, ConfirmationExpired);
        }

        // Already confirmed, declined, or swept to declined by the staleness
        // sweeper. Only a still-pending call may be confirmed — otherwise a
        // double-confirm would replay a destructive op.
        if (call.Status != AiConversationVocabulary.PendingConfirmation)
        {
            throw AppException.NotFound(PendingCallNotFoundCode, ConfirmationAlreadyHandled);
        }

        return call;
    }
}
