using System.Globalization;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Notifications;

/// <summary>
/// The wording of a fired reminder — <c>reminderWorker.ts</c>'s <c>shortDate</c>
/// and <c>reminderBody</c>.
///
/// <para>
/// These three strings ship to the user's lock screen, so they are copied
/// verbatim, em dash and full stop included. All three were captured live from
/// the dev reference (<c>:4100</c>, workers ON) rather than read off the source.
/// </para>
/// </summary>
public static class ReminderNotificationText
{
    /// <summary>A reminder on a matter with no due date. No date to name, so none is invented.</summary>
    public const string NoDueDateBody = "Reminder";

    /// <summary>
    /// <c>d.toLocaleDateString('en-US', {month: 'short', day: 'numeric', timeZone})</c>
    /// — e.g. <c>"Mar 4"</c>.
    ///
    /// <para>
    /// <b>The zone is the whole point.</b> A matter due at
    /// <c>2026-03-05T01:00Z</c> is "Mar 5" in UTC but "Mar 4" for a user in New
    /// York, and a reminder that names the wrong day is worse than one that names
    /// none.
    /// </para>
    ///
    /// <para>
    /// An unrecognised zone THROWS, mirroring the uncaught <c>Intl</c> RangeError
    /// in Node — see the note on the caller. There is deliberately no silent UTC
    /// fallback (KERNEL.md §8.1, §8.4): guessing UTC for a user in Cairo moves
    /// every date invisibly.
    /// </para>
    /// </summary>
    public static string ShortDate(DateTime instant, string timeZone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(instant, DateTimeKind.Utc), zone);

        // "MMM d" under en-US gives the abbreviated month and a non-padded day,
        // which is what Intl's {month:'short', day:'numeric'} produces.
        return local.ToString("MMM d", CultureInfo.GetCultureInfo("en-US"));
    }

    /// <summary>
    /// The notification body. <c>lead</c> reads as an advance warning; every other
    /// kind (<c>due</c>, <c>ai</c>) reads as the due announcement.
    /// </summary>
    public static string Body(DateTime? dueAt, string reminderKind, string timeZone)
    {
        if (dueAt is not { } due)
        {
            return NoDueDateBody;
        }

        var day = ShortDate(due, timeZone);

        // U+2014 EM DASH, not a hyphen. Verified byte-for-byte against :4100.
        return reminderKind == ReminderKinds.Lead
            ? $"Coming up — due {day}."
            : $"Due {day}.";
    }

    /// <summary>The <c>kind</c> values a reminder entry can carry; mirrors <see cref="TaskVocabulary.ReminderKinds"/>.</summary>
    private static class ReminderKinds
    {
        public const string Lead = "lead";
    }
}
