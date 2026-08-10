using Life_Admin_Autopilot.BLL.Features.Notifications;

namespace Life_Admin_Autopilot.Tests.Features.Notifications;

/// <summary>
/// The three strings a fired reminder can carry.
///
/// <para>
/// All three were captured from the DEV reference (<c>:4100</c>, workers ON) —
/// <c>:4200</c> early-returns from every worker, so the notification is never
/// written there and the wording cannot be observed.
/// </para>
/// </summary>
public sealed class ReminderNotificationTextTests
{
    /// <summary>2026-03-05T01:00Z is "Mar 5" in UTC but "Mar 4" in New York.</summary>
    private static readonly DateTime AcrossMidnight = new(2026, 3, 5, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void names_the_day_in_the_users_zone_not_in_utc()
    {
        // The whole reason the worker does a User lookup at all. A reminder that
        // names the wrong day is worse than one that names none.
        Assert.Equal("Mar 4", ReminderNotificationText.ShortDate(AcrossMidnight, "America/New_York"));
        Assert.Equal("Mar 5", ReminderNotificationText.ShortDate(AcrossMidnight, "UTC"));
    }

    [Fact]
    public void formats_the_day_without_a_leading_zero()
    {
        // Intl's {day:'numeric'} is not zero-padded.
        Assert.Equal("Sep 4", ReminderNotificationText.ShortDate(new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc), "UTC"));
        Assert.Equal("Sep 14", ReminderNotificationText.ShortDate(new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc), "UTC"));
    }

    [Fact]
    public void uses_the_en_us_abbreviated_month_regardless_of_the_ambient_culture()
    {
        // The string is 'en-US' in Node no matter who reads it, so a machine or a
        // test host running under another culture must not change it.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal("Mar 4", ReminderNotificationText.ShortDate(AcrossMidnight, "America/New_York"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void throws_on_an_unrecognised_zone_rather_than_falling_back_to_utc()
    {
        // Mirrors Node's uncaught Intl RangeError. A silent UTC fallback would move
        // the named day invisibly for anyone east or west of it — KERNEL.md §8.1.
        Assert.ThrowsAny<Exception>(() => ReminderNotificationText.ShortDate(AcrossMidnight, "Mars/Olympus_Mons"));
    }

    [Fact]
    public void reads_a_lead_reminder_as_an_advance_warning()
    {
        // U+2014 EM DASH, not a hyphen.
        Assert.Equal(
            "Coming up — due Mar 4.",
            ReminderNotificationText.Body(AcrossMidnight, "lead", "America/New_York"));
    }

    [Fact]
    public void reads_every_other_kind_as_the_due_announcement()
    {
        Assert.Equal("Due Mar 4.", ReminderNotificationText.Body(AcrossMidnight, "due", "America/New_York"));
        Assert.Equal("Due Mar 4.", ReminderNotificationText.Body(AcrossMidnight, "ai", "America/New_York"));
    }

    [Fact]
    public void says_only_reminder_when_the_matter_has_no_due_date()
    {
        // No date to name, so none is invented — and the zone is never consulted.
        Assert.Equal("Reminder", ReminderNotificationText.Body(null, "lead", "Mars/Olympus_Mons"));
    }
}
