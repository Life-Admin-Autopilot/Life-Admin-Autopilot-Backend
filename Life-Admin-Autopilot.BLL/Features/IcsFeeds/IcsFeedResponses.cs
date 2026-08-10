using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <summary>
/// Wire shape of an <c>IcsFeed</c> — the output of the model's <c>toJSON</c>
/// transform.
///
/// <para>
/// <b>The cache validators are STRIPPED.</b> <c>toJSON</c> deletes <c>etag</c> and
/// <c>lastModified</c> along with <c>_id</c> and <c>__v</c>: they are plumbing, they
/// mean nothing to a client, and exposing them only invites someone to try to "fix"
/// a stuck sync by editing them. Serializing the document directly would leak both.
/// </para>
///
/// <para>
/// Every optional member is an ABSENT KEY when unset, never <c>null</c> — Mongoose
/// omits unset fields entirely and the frontend distinguishes the two.
/// </para>
/// </summary>
public sealed class IcsFeedDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    /// <summary>Every attempt, successful or not.</summary>
    [JsonPropertyName("lastFetchedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastFetchedAt { get; init; }

    /// <summary>Only attempts that returned a body we parsed.</summary>
    [JsonPropertyName("lastSyncedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastSyncedAt { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = IcsFeedVocabulary.Active;

    [JsonPropertyName("lastError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; init; }

    [JsonPropertyName("failureCount")]
    public int FailureCount { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}

/// <summary>
/// The outcome of one synchronous poll.
///
/// <para>
/// An unhealthy feed is reported HERE, with HTTP 200/201 — it is not an HTTP error.
/// The subscription itself succeeded; the publisher is what is broken, and the UI
/// needs the row plus the reason rather than a failed request.
/// </para>
/// </summary>
public sealed class IcsSyncResultDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    [JsonPropertyName("created")]
    public int Created { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("needingConfirmation")]
    public int NeedingConfirmation { get; init; }

    /// <summary>Present only for <c>gone</c> and <c>error</c>.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

public sealed class IcsFeedSyncResponse
{
    [JsonPropertyName("feed")]
    public IcsFeedDto Feed { get; init; } = new();

    [JsonPropertyName("sync")]
    public IcsSyncResultDto Sync { get; init; } = new();
}

public sealed class IcsFeedListResponse
{
    [JsonPropertyName("feeds")]
    public IReadOnlyList<IcsFeedDto> Feeds { get; init; } = Array.Empty<IcsFeedDto>();
}

public sealed class IcsDeleteResponse
{
    [JsonPropertyName("removed")]
    public bool Removed { get; init; } = true;

    /// <summary>
    /// <c>modifiedCount</c> of the soft-delete sweep over this feed's FUTURE OPEN
    /// matters only.
    /// </summary>
    [JsonPropertyName("retiredMatters")]
    public long RetiredMatters { get; init; }
}

public static class IcsFeedMappers
{
    public static IcsFeedDto ToDto(this IcsFeedDocument doc) => new()
    {
        UserId = doc.UserId.ToId(),
        Url = doc.Url,
        Label = doc.Label,
        Domain = doc.Domain,
        LastFetchedAt = doc.LastFetchedAt,
        LastSyncedAt = doc.LastSyncedAt,
        Status = doc.Status,
        LastError = doc.LastError,
        FailureCount = doc.FailureCount,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt,
        Id = doc.Id.ToId(),
    };

    public static IcsSyncResultDto ToDto(this SyncFeedResult result) => new()
    {
        Status = result.Status,
        Created = result.Created,
        Updated = result.Updated,
        NeedingConfirmation = result.NeedingConfirmation,
        Reason = result.Reason,
    };
}
