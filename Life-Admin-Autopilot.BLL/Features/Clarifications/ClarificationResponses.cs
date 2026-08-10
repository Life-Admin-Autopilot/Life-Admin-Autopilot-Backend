using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// <c>GET /me/clarifications</c> — <c>{clarifications, hasMore, nextCursor}</c>.
/// </summary>
public sealed class ClarificationListResponse
{
    [JsonPropertyName("clarifications")]
    public IReadOnlyList<ClarificationDto> Clarifications { get; init; } = Array.Empty<ClarificationDto>();

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    /// <summary>
    /// The last item's <c>createdAt</c>, or an explicit <c>null</c> on the final page.
    ///
    /// <para>
    /// Node builds this in plain JavaScript (<c>hasMore ? … : null</c>), so the key is
    /// ALWAYS present. The kernel pins <c>WhenWritingNull</c> globally — right for
    /// document DTOs, wrong here — so this opts back in.
    /// </para>
    /// </summary>
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? NextCursor { get; init; }
}

/// <summary>
/// <c>POST /me/clarifications/{id}/defer</c> and <c>/drop</c> — <c>{clarification}</c>.
/// </summary>
public sealed class ClarificationEnvelope
{
    [JsonPropertyName("clarification")]
    public ClarificationDto Clarification { get; init; } = new();
}

/// <summary>
/// <c>POST /me/clarifications/{id}/resolve</c> — <c>{clarification, task}</c>.
/// </summary>
public sealed class ClarificationResolveResponse
{
    [JsonPropertyName("clarification")]
    public ClarificationDto Clarification { get; init; } = new();

    /// <summary>
    /// The updated task, RAW — <c>runTool</c>'s result carries no i18n overlay.
    ///
    /// <para>
    /// Explicitly <c>null</c> (never omitted) when the question was already closed,
    /// or when its task had been deleted and the question was therefore dropped.
    /// </para>
    /// </summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TaskDto? Task { get; init; }
}
