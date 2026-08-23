using System.Globalization;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// A server-raised chat question, written in the language the user was speaking.
///
/// <para>
/// <b>Why the chat lane does not send an i18n key.</b> A key renders in whatever
/// language the APP is set to, which is the right answer for voice — that lane is
/// fire-and-forget and nobody knows what language the note was in until it is
/// transcribed. Chat does know: the matter's title is the user's own words. Keyed,
/// an Arabic conversation in an English-language app produced a card with the
/// model's question in Arabic and the server's directly underneath it in English.
/// Both halves were behaving as specified, which is what made it hard to see.
/// </para>
///
/// <para>
/// So the rule is now one line: a row carries a key only when nobody can tell what
/// language the user was speaking. Chat rows carry finished text, exactly as the
/// model's own questions always have; voice rows keep their keys.
/// <see cref="ChatGapText"/> is the one place that turns the second into the first.
/// </para>
///
/// <para>
/// <b>The strings are the app's own, copied.</b> They have to match what the
/// client would have rendered from the same key, or the two lanes would ask one
/// question in two voices — see <c>lib/i18n/messages/{ar,en}/uncertainty.json</c>
/// in the mobile repo. A key with no entry here is left alone and renders its
/// English fallback, which is the same thing an unknown key does on the client.
/// </para>
/// </summary>
public static class ChatGapText
{
    /// <summary>
    /// Egyptian Arabic and British English, matching the two catalogues the app
    /// ships. `{title}` is the matter's own words; `{day}` and `{time}` are derived
    /// from the option's `at` instant, the way the client derives them.
    /// </summary>
    private static readonly Dictionary<string, (string En, string Ar)> Templates =
        new(StringComparer.Ordinal)
        {
            ["ask.whenDue"] = ("When is “{title}” due?", "متى موعد «{title}»؟"),
            ["ask.howMuch"] = ("How much is “{title}”?", "كم قيمة «{title}»؟"),
            ["ask.whatTimeOn"] = ("What time on {day}?", "الساعة كام يوم {day}؟"),
            ["chip.noDateNeeded"] = ("No date needed", "لا حاجة لموعد"),
            ["chip.tomorrowAt"] = ("Tomorrow — {time}", "بكرة — {time}"),
            ["chip.dayAt"] = ("{day} — {time}", "{day} الساعة {time}"),
            ["chip.at"] = ("{time}", "{time}"),
            ["chip.morning"] = ("Morning — {time}", "صباحًا — {time}"),
            ["chip.afternoon"] = ("Afternoon — {time}", "بعد الظهر — {time}"),
            ["chip.evening"] = ("Evening — {time}", "مساءً — {time}"),
        };

    /// <summary>
    /// One Arabic letter is enough. The test is on the TITLE, which is the user's
    /// own sentence rather than anything this server composed, and a title mixing
    /// scripts — "دفع فاتورة Vodafone" — is a sentence the user typed in Arabic.
    /// </summary>
    private static readonly Regex ArabicLetter = new(@"\p{IsArabic}", RegexOptions.Compiled);

    public static bool LooksArabic(string? text) =>
        !string.IsNullOrEmpty(text) && ArabicLetter.IsMatch(text);

    /// <summary>
    /// The same question, finished, in the matter's language — and with the keys
    /// dropped, because a key present on the row is what makes the client re-render
    /// it in the app's language and undo this.
    /// </summary>
    public static DraftClarification InTheLanguageOfTheMatter(
        DraftClarification gap,
        string title,
        string? timezone)
    {
        var arabic = LooksArabic(title);
        var culture = CultureFor(arabic);

        return new DraftClarification(
            Render(gap.Question, gap.QuestionKey, gap.QuestionParams, title, timezone, arabic, culture),
            gap.Kind,
            gap.CostOfWrong,
            gap.Options
                .Select(option => new DraftClarifyOption(
                    Render(option.Label, option.LabelKey, option.LabelParams, title, timezone, arabic, culture),
                    option.DueAt))
                .ToList());
    }

    /// <summary>
    /// One replacement chip's label — "الاثنين 31 أغسطس الساعة 9:00 ص" — for a time
    /// <see cref="ChipAvailability"/> found free after dropping one that was taken.
    /// Same template and same clock as the chips it stands in for, so a refilled
    /// question does not read as two features.
    /// </summary>
    public static string ChipLabelFor(DateTime instant, string title, string? timezone)
    {
        var arabic = LooksArabic(title);

        return Render(
            fallback: instant.ToString("u"),
            key: "chip.dayAt",
            parameters: new Dictionary<string, string> { ["at"] = instant.ToString("o") },
            title: title,
            timezone: timezone,
            arabic: arabic,
            culture: CultureFor(arabic));
    }

    /// <summary>
    /// <c>ar-EG</c> and <c>en-GB</c>, matching the two catalogues — with English's
    /// meridiem forced upper-case.
    ///
    /// <para>
    /// ICU gives en-GB a lower-case "am"/"pm" on Linux and an upper-case one on
    /// Windows, so the same chip read "9:00 am" from the deployed server and
    /// "9:00 AM" from a developer's machine — and, worse, "9:00 AM" from the client
    /// beside it, because <c>Intl</c> upper-cases. Pinned here, all three agree.
    /// </para>
    /// </summary>
    private static CultureInfo CultureFor(bool arabic)
    {
        if (arabic)
        {
            return CultureInfo.GetCultureInfo("ar-EG");
        }

        var english = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        english.DateTimeFormat.AMDesignator = "AM";
        english.DateTimeFormat.PMDesignator = "PM";
        return english;
    }

    private static string Render(
        string fallback,
        string? key,
        IReadOnlyDictionary<string, string>? parameters,
        string title,
        string? timezone,
        bool arabic,
        CultureInfo culture)
    {
        if (key is null || !Templates.TryGetValue(key, out var template))
        {
            return fallback;
        }

        var text = (arabic ? template.Ar : template.En).Replace("{title}", title, StringComparison.Ordinal);

        if (parameters is not null
            && parameters.TryGetValue("at", out var raw)
            && DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var at))
        {
            var local = ToLocal(at, timezone);
            text = text
                .Replace("{day}", local.ToString("dddd d MMMM", culture), StringComparison.Ordinal)
                .Replace("{time}", local.ToString("h:mm tt", culture), StringComparison.Ordinal);
        }

        // A placeholder we could not fill would reach the user as literal braces.
        // The English sentence the generator already wrote is a worse answer than
        // Arabic and a much better one than "بكرة — {time}".
        return text.Contains('{', StringComparison.Ordinal) ? fallback : text;
    }

    /// <summary>
    /// An unresolvable zone yields UTC rather than throwing. It cannot happen on the
    /// chat lane — the hold binder rejects an unknown IANA name before this runs —
    /// but a question is not worth a 500 if that ever stops being true.
    /// </summary>
    private static DateTime ToLocal(DateTime utc, string? timezone)
    {
        if (string.IsNullOrEmpty(timezone))
        {
            return utc;
        }

        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(timezone));
        }
        catch (TimeZoneNotFoundException)
        {
            return utc;
        }
        catch (InvalidTimeZoneException)
        {
            return utc;
        }
    }
}
