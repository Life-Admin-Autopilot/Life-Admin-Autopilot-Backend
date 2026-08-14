using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>
/// When a matter clashes, propose times it could move to instead.
///
/// <para>
/// <b>Deterministic, not a model call.</b> The obvious implementation is to ask
/// the planning model, but this runs on every clash the user hits while nudging
/// a time around — the one interaction where they will try five slots in a row.
/// A free-tier model with a 20-request daily cap would be exhausted by a single
/// stubborn reschedule, and the suggestion would arrive a second late every
/// time. Keywords are worse at nuance and better at everything else that matters
/// here: instant, free, offline, and identical on every run.
/// </para>
///
/// <para>
/// The reasoning it does encode is the one the user actually asked for: a matter
/// belongs to a part of the day. Proposing 09:00 for a film is not merely
/// unhelpful, it is obviously wrong to a human reading it, and an assistant that
/// suggests it looks like it is not listening.
/// </para>
/// </summary>
public static class SlotSuggester
{
    /// <summary>Where in the day a kind of matter naturally sits, as local hours.</summary>
    private readonly record struct DayPart(int EarliestHour, int LatestHour, string Why);

    // Evening things, morning things, and working-hours things. Matched on the
    // title in BOTH languages, because the title is kept in whatever the user
    // spoke and an English-only matcher would silently never fire for half the
    // users this ships to.
    private static readonly (string[] Words, DayPart Part)[] Profiles =
    {
        (new[]
        {
            "movie", "film", "cinema", "match", "game", "concert", "dinner", "party",
            "فيلم", "سينما", "ماتش", "مباراة", "حفلة", "عشاء", "سهرة",
        }, new DayPart(18, 22, "evening")),

        (new[]
        {
            "gym", "workout", "run", "training", "jog", "swim",
            "جيم", "تمرين", "جري", "سباحة",
        }, new DayPart(17, 21, "evening")),

        (new[]
        {
            "breakfast", "fajr", "sunrise",
            "فطار", "الفجر",
        }, new DayPart(7, 9, "early")),

        (new[]
        {
            "doctor", "dentist", "clinic", "hospital", "bank", "lecture", "exam",
            "class", "lesson", "meeting", "interview", "appointment", "renew", "office",
            "دكتور", "طبيب", "عيادة", "مستشفى", "بنك", "محاضرة", "امتحان",
            "درس", "اجتماع", "مقابلة", "موعد", "تجديد", "مصلحة",
        }, new DayPart(9, 16, "working hours")),
    };

    /// <summary>Anything unrecognised. Daytime, but not dawn and not late night.</summary>
    private static readonly DayPart Default = new(10, 19, "daytime");

    /// <summary>
    /// Times this matter could move to, soonest first, none of which clash.
    /// </summary>
    /// <param name="title">Used to infer the part of day. Language-agnostic.</param>
    /// <param name="desired">What the user asked for — the search starts here.</param>
    /// <param name="offset">The user's UTC offset, so "evening" means their evening.</param>
    /// <param name="clashes">
    /// Returns true when a candidate instant collides with something. Injected
    /// rather than computed here so the caller owns the pool and the window, and
    /// a suggestion can never disagree with the check that will run at save.
    /// </param>
    public static IReadOnlyList<DateTime> Suggest(
        string title,
        DateTime desired,
        TimeSpan offset,
        Func<DateTime, bool> clashes,
        int wanted = 3)
    {
        var part = PartFor(title);
        var found = new List<DateTime>();

        // Walk the next few days at half-hour steps, staying inside the matter's
        // own part of the day. Half-hours because a suggestion of 18:17 reads as
        // machine output; people schedule on the hour and the half.
        for (var day = 0; day <= 6 && found.Count < wanted; day++)
        {
            var localDay = (desired + offset).Date.AddDays(day);

            for (var half = part.EarliestHour * 2; half <= part.LatestHour * 2 && found.Count < wanted; half++)
            {
                var localSlot = localDay.AddMinutes(half * 30);
                var instant = localSlot - offset;

                // Never propose the past, and never propose the time they already
                // have — that is the one slot we know does not work.
                if (instant <= DateTime.UtcNow) continue;
                if (instant == desired) continue;
                if (clashes(instant)) continue;

                found.Add(instant);
            }
        }

        return found;
    }

    /// <summary>The label for why these times were chosen, for the UI to show.</summary>
    public static string ReasonFor(string title) => PartFor(title).Why;

    private static DayPart PartFor(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Default;
        var lowered = title.ToLowerInvariant();

        foreach (var (words, part) in Profiles)
        {
            foreach (var word in words)
            {
                if (lowered.Contains(word, StringComparison.OrdinalIgnoreCase)) return part;
            }
        }

        return Default;
    }
}
