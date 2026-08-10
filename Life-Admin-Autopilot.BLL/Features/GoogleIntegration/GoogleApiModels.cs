using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>The slice of Google's Event resource this importer reads.</summary>
public sealed class GoogleCalendarEvent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("start")]
    public GoogleEventDateTime? Start { get; set; }

    [JsonPropertyName("reminders")]
    public GoogleEventReminders? Reminders { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("transparency")]
    public string? Transparency { get; set; }

    [JsonPropertyName("recurringEventId")]
    public string? RecurringEventId { get; set; }

    [JsonPropertyName("recurrence")]
    public List<string>? Recurrence { get; set; }

    [JsonPropertyName("attendees")]
    public List<GoogleEventPerson>? Attendees { get; set; }

    [JsonPropertyName("organizer")]
    public GoogleEventPerson? Organizer { get; set; }

    [JsonPropertyName("creator")]
    public GoogleEventPerson? Creator { get; set; }
}

public sealed class GoogleEventDateTime
{
    /// <summary>RFC3339 with a real offset — unambiguous, nothing to assume.</summary>
    [JsonPropertyName("dateTime")]
    public string? DateTime { get; set; }

    /// <summary>All-day form, <c>YYYY-MM-DD</c>. Goes through the date-only policy.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }
}

public sealed class GoogleEventReminders
{
    [JsonPropertyName("useDefault")]
    public bool? UseDefault { get; set; }

    [JsonPropertyName("overrides")]
    public List<GoogleEventReminderOverride>? Overrides { get; set; }
}

public sealed class GoogleEventReminderOverride
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("minutes")]
    public int? Minutes { get; set; }
}

public sealed class GoogleEventPerson
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>Set by Google against the AUTHENTICATED user. Authoritative; never compare emails.</summary>
    [JsonPropertyName("self")]
    public bool? Self { get; set; }

    [JsonPropertyName("responseStatus")]
    public string? ResponseStatus { get; set; }
}

public sealed class GoogleEventsPage
{
    [JsonPropertyName("items")]
    public List<GoogleCalendarEvent>? Items { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("nextSyncToken")]
    public string? NextSyncToken { get; set; }
}

public sealed class GoogleTaskList
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public sealed class GoogleTaskListsPage
{
    [JsonPropertyName("items")]
    public List<GoogleTaskList>? Items { get; set; }
}

public sealed class GoogleTaskItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// DATE-ONLY by documented design: "the time portion of the timestamp is
    /// discarded", and reading or writing a time is impossible, not merely
    /// unsupported. Arrives as a full RFC3339 stamp whose time part is always zeroed.
    /// </summary>
    [JsonPropertyName("due")]
    public string? Due { get; set; }

    [JsonPropertyName("deleted")]
    public bool? Deleted { get; set; }

    [JsonPropertyName("hidden")]
    public bool? Hidden { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }
}

public sealed class GoogleTasksPage
{
    [JsonPropertyName("items")]
    public List<GoogleTaskItem>? Items { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}
