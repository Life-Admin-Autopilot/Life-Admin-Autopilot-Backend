using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.Notifications;

/// <summary><c>GET /me/notifications</c> — <c>{notifications, unreadCount}</c>.</summary>
public sealed class NotificationFeedResponse
{
    [JsonPropertyName("notifications")]
    public IReadOnlyList<NotificationDto> Notifications { get; init; } = Array.Empty<NotificationDto>();

    [JsonPropertyName("unreadCount")]
    public long UnreadCount { get; init; }
}

/// <summary><c>POST /me/notifications/read</c> — <c>{ok: true, unreadCount}</c>.</summary>
public sealed class NotificationsReadResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    [JsonPropertyName("unreadCount")]
    public long UnreadCount { get; init; }
}

/// <summary>
/// One entry of <c>GET /me/reminders/upcoming</c> — a local notification the
/// DEVICE will schedule.
/// </summary>
public sealed class UpcomingReminderDto
{
    /// <summary>
    /// <c>`${taskId}:${epochMillis}`</c>. Deterministic on purpose: iOS keys pending
    /// local notifications by identifier, so a re-sync REPLACES the same entry
    /// instead of stacking a duplicate beside it.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    /// <summary>The task's CANONICAL title — this route applies no i18n overlay.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTime At { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// The PARENT TASK's <c>dueAt</c>, or an explicit <c>null</c>.
    ///
    /// <para>
    /// Node builds this entry in plain JavaScript (<c>?? null</c>), not through
    /// Mongoose, so the key is always present. The kernel pins
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> globally — correct for
    /// document-shaped DTOs, wrong here — so this opts back in explicitly.
    /// </para>
    /// </summary>
    [JsonPropertyName("dueAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? DueAt { get; init; }
}

/// <summary><c>GET /me/reminders/upcoming</c> — <c>{reminders}</c>.</summary>
public sealed class UpcomingRemindersResponse
{
    [JsonPropertyName("reminders")]
    public IReadOnlyList<UpcomingReminderDto> Reminders { get; init; } = Array.Empty<UpcomingReminderDto>();
}
