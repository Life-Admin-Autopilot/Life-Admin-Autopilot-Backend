using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.Ai;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// Multi-conversation support: the set of threads a user has, and which one a
/// turn belongs to.
///
/// <para>
/// <b>Agent memory is per-thread for free.</b> Langflow's session key is derived
/// from the conversation document's own <c>SessionGeneration</c>, and each thread
/// IS a separate document — so switching threads switches the agent's memory with
/// it, and no thread can answer out of another's history. Nothing here has to
/// arrange that; it falls out of the existing key.
/// </para>
/// </summary>
public sealed class AiConversationThreadService
{
    private const string Scope = AiConversationVocabulary.PersonalScope;

    private readonly AiConversationThreadRepository _threads;

    public AiConversationThreadService(AiConversationThreadRepository threads)
    {
        _threads = threads;
    }

    public async Task<IReadOnlyList<AiThreadSummaryDto>> ListAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        var documents = await _threads.ListAsync(userId, Scope, cancellationToken).ConfigureAwait(false);

        return documents.Select(ToSummary).ToList();
    }

    public async Task<AiThreadSummaryDto> CreateAsync(
        ObjectId userId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var created = await _threads
            .CreateAsync(userId, Scope, title, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return ToSummary(created);
    }

    public Task<bool> ExistsAsync(
        ObjectId userId,
        string conversationId,
        CancellationToken cancellationToken = default) =>
        _threads.ExistsAsync(userId, Scope, conversationId, cancellationToken);

    public Task<bool> RenameAsync(
        ObjectId userId,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default) =>
        _threads.RenameAsync(userId, Scope, conversationId, title, cancellationToken: cancellationToken);

    public Task<bool> DeleteAsync(
        ObjectId userId,
        string conversationId,
        CancellationToken cancellationToken = default) =>
        _threads.DeleteAsync(userId, Scope, conversationId, cancellationToken);

    public Task TitleFromFirstMessageAsync(
        ObjectId userId,
        string? conversationId,
        string text,
        CancellationToken cancellationToken = default) =>
        _threads.TitleFromFirstMessageAsync(userId, Scope, conversationId, text, cancellationToken);

    /// <summary>
    /// Which thread a turn belongs to, opening one if the user has none.
    ///
    /// <para>
    /// A client naming a thread it does not own gets its own most recent thread
    /// rather than an error: the id is opaque to the user, so the only ways to get
    /// here are a deleted thread racing an in-flight send or a stale cache — and
    /// dropping the user's sentence on the floor is a worse answer than filing it
    /// somewhere they can find it.
    /// </para>
    /// </summary>
    public async Task<string> ResolveAsync(
        ObjectId userId,
        string? requested,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requested)
            && await ExistsAsync(userId, requested, cancellationToken).ConfigureAwait(false))
        {
            return requested;
        }

        var existing = await _threads.ListAsync(userId, Scope, cancellationToken).ConfigureAwait(false);
        var mostRecent = existing.FirstOrDefault();
        if (mostRecent?.ScopeId is { } id)
        {
            return id;
        }

        var created = await _threads
            .CreateAsync(userId, Scope, null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return created.ScopeId!;
    }

    private static AiThreadSummaryDto ToSummary(AiConversationDocument document)
    {
        var last = document.Messages.Count > 0 ? document.Messages[^1] : null;

        return new AiThreadSummaryDto
        {
            Id = document.ScopeId ?? string.Empty,
            Title = document.Title,
            Preview = AiConversationThreadRepository.Summarize(last?.Text, 120),
            MessageCount = document.Messages.Count,
            UpdatedAt = document.UpdatedAt,
        };
    }
}

/// <summary>A row in the history drawer.</summary>
public sealed class AiThreadSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Null until the thread's first turn names it. Readers show a placeholder.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Title { get; init; }

    [JsonPropertyName("preview")]
    public string Preview { get; init; } = string.Empty;

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>The <c>GET /ai/conversations</c> envelope.</summary>
public sealed class AiThreadListResponse
{
    [JsonPropertyName("conversations")]
    public IReadOnlyList<AiThreadSummaryDto> Conversations { get; init; } = Array.Empty<AiThreadSummaryDto>();
}

/// <summary>One thread's transcript — <c>GET /ai/conversations/{id}</c>.</summary>
public sealed class AiThreadResponse
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = AiConversationVocabulary.PersonalScope;

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Title { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<AiConversationMessageDto> Messages { get; init; } = Array.Empty<AiConversationMessageDto>();
}
