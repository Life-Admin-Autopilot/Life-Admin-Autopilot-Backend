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

    /// <summary>
    /// How pressing this reminder is relative to the others in the same payload —
    /// <c>0</c> to <c>4</c>, see <see cref="Life_Admin_Autopilot.BLL.Kernel.Reminders.ReminderUrgency"/>.
    ///
    /// <para>
    /// <b>Node does not send this field.</b> It is the one addition to a ported
    /// response shape in this slice; the reasoning and its measured parity cost are
    /// recorded in <c>docs/DIVERGENCES.md</c>.
    /// </para>
    ///
    /// <para>
    /// Advisory. The array is still ordered by <c>at</c> and still capped at the
    /// soonest 60, because the device schedules against a clock and an out-of-order
    /// schedule would help nobody. This is here so a client can decide what to make
    /// PROMINENT among reminders it holds at once.
    /// </para>
    /// </summary>
    [JsonPropertyName("urgencyScore")]
    public double UrgencyScore { get; init; }
}

/// <summary><c>GET /me/reminders/upcoming</c> — <c>{reminders}</c>.</summary>
public sealed class UpcomingRemindersResponse
{
    [JsonPropertyName("reminders")]
    public IReadOnlyList<UpcomingReminderDto> Reminders { get; init; } = Array.Empty<UpcomingReminderDto>();
}
