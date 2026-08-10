namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>What a calendar event is TO Kitto.</summary>
public enum EventRole
{
    /// <summary>Something you have to DO. Gets the full treatment: domain, lead-time nudge, reminders.</summary>
    Matter,

    /// <summary>Something already handled. Counted as occupied time, but never nudged.</summary>
    Commitment,

    /// <summary>Noise. A working-location marker is one per day; a birthday is not a task.</summary>
    Ignore,
}

/// <summary>
/// Decide what a calendar event is, entirely in code. Port of
/// <c>server/src/modules/integrations/google/triageEvent.ts</c>.
///
/// <para>
/// A busy calendar is thousands of events per user. Asking a model about each one
/// would land directly on the product's margin, so every signal used here is read
/// straight off the Event payload.
/// </para>
///
/// <para>
/// The dividing line between matter and commitment is <b>"did anyone else agree to
/// this"</b>. An event with attendees has its own reminder machinery and its own
/// social accountability. An event you put in your own calendar alone is a note to
/// self, and a note to self is precisely what Kitto is for.
/// </para>
/// </summary>
public static class GoogleEventTriage
{
    /// <summary>
    /// eventTypes that are never an obligation. <c>workingLocation</c> is the big
    /// one — Google emits one per working day per user, so treating them as matters
    /// would bury the real list under "Office" entries.
    /// </summary>
    private static readonly HashSet<string> IgnoredEventTypes = new(StringComparer.Ordinal)
    {
        "workingLocation", "birthday", "focusTime", "outOfOffice",
    };

    public static EventRole Triage(GoogleCalendarEvent e)
    {
        // Cancelled instances arrive through incremental sync as tombstones. They are
        // how a deletion is communicated, not something to file.
        if (e.Status == "cancelled")
        {
            return EventRole.Ignore;
        }

        if (!string.IsNullOrEmpty(e.EventType) && IgnoredEventTypes.Contains(e.EventType))
        {
            return EventRole.Ignore;
        }

        // The user marked themselves free. They are telling us this does not occupy
        // them, so it is neither a task nor a busy block.
        if (e.Transparency == "transparent")
        {
            return EventRole.Ignore;
        }

        // Someone else agreed to be there. The invite carries its own reminders, and a
        // second nudge from Kitto is notification pile-up.
        if (e.Attendees is { Count: > 0 })
        {
            return EventRole.Commitment;
        }

        // Recurring means routine. A weekly slot you already know about does not need
        // a lead-time nudge every single week.
        if (!string.IsNullOrEmpty(e.RecurringEventId) || e.Recurrence is { Count: > 0 })
        {
            return EventRole.Commitment;
        }

        // Someone else put this in your calendar. Even with no attendee list, it is
        // not a note to self.
        //
        // `self` is checked rather than comparing emails: Google sets it against the
        // authenticated user, which is authoritative in a way string comparison is not
        // — people have aliases, and a mismatched alias would misfile every event they
        // own. When organizer is absent entirely we fall through to `matter`, because
        // a personal calendar's own entries frequently omit it.
        if (e.Organizer is not null && e.Organizer.Self == false)
        {
            return EventRole.Commitment;
        }

        if (e.Organizer is null && e.Creator is not null && e.Creator.Self == false)
        {
            return EventRole.Commitment;
        }

        return EventRole.Matter;
    }
}
