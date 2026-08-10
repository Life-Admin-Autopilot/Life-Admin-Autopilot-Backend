using System.Globalization;
using Life_Admin_Autopilot.DAL.Features.Digest;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>
/// What the digest says when there is no model to say it better — and, on a server
/// with no AI configured, what it always says.
///
/// <para>
/// Factual and finished: it reports the day, it does not grade the person. Nothing
/// here counts what the user failed to do, because the one line at the top of the
/// app is the worst possible place to put that.
/// </para>
///
/// <para>
/// First match wins. The order is load-bearing — <c>completedToday</c> outranks
/// <c>needsInput</c>, so a day with closed matters and open questions leads with
/// the closures.
/// </para>
/// </summary>
public static class NeutralHeadline
{
    public static string For(DailyDigestCountsDocument counts)
    {
        if (counts.DueToday > 0)
        {
            return $"{Plural(counts.DueToday, "matter", "matters")} due today.";
        }

        if (counts.CompletedToday > 0)
        {
            return $"Nothing due today. {Plural(counts.CompletedToday, "matter", "matters")} closed.";
        }

        if (counts.NeedsInput > 0)
        {
            return $"Nothing due today. {Plural(counts.NeedsInput, "question is", "questions are")} waiting on you.";
        }

        return counts.OpenTotal == 0 ? "Nothing on today." : "Nothing due today.";
    }

    private static string Plural(int n, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");
}
