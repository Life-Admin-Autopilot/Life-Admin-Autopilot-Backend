using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Kernel.Reminders;

/// <summary>
/// How long a matter takes to DO — the duration half of
/// <c>docs/smart-reminder-conflict-spec.md</c> §3.2.
///
/// <para>
/// <b>Not the same thing as <see cref="ReminderLeadTime"/>.</b> That answers how
/// much WARNING a matter deserves, in days. This answers how much TIME it needs, in
/// minutes. A car insurance renewal earns 30 days of warning and takes 45 minutes to
/// actually do; the two numbers are unrelated and are kept apart deliberately.
/// </para>
///
/// <para>
/// <b>Derived, never stored.</b> The task's own <c>estimate</c> wins whenever it has
/// one; otherwise a default is computed on read from the same keyword rows the
/// lead-time table uses, falling back to the domain. Nothing here is written back to
/// the database, so <c>GET /me/tasks</c> keeps sending exactly what Node sends and
/// the <c>source</c> enum keeps its two ported values (<c>ai</c>, <c>user</c>) rather
/// than gaining a third that would show up on every matter.
/// </para>
///
/// <para>
/// <b>Where a guess is and is not allowed.</b> Inference is already the norm in the
/// reminder path — the entire lead-time table is a guess about matters nobody
/// estimated. It is NOT the norm in the daily digest, which deliberately contributes
/// zero for an unestimated matter rather than a fabricated number
/// (<c>DailyDigestComputer.ReadEstimate</c>). That decision stands; this class is
/// used for scheduling only.
/// </para>
/// </summary>
public static class ReminderDuration
{
    /// <summary>
    /// What a matter takes when neither its estimate nor its title nor its domain
    /// says anything. On the bucket ladder, like every other value here.
    /// </summary>
    public const int FallbackMinutes = 30;

    /// <summary>
    /// Minutes per keyword row. Keyed off <see cref="ReminderLeadTime.MatchKeyword"/>
    /// so the eight patterns exist once.
    /// </summary>
    private static readonly IReadOnlyDictionary<ReminderLeadTime.MatterKeyword, int> KeywordMinutes =
        new Dictionary<ReminderLeadTime.MatterKeyword, int>
        {
            // Forms, photographs and a counter somewhere.
            [ReminderLeadTime.MatterKeyword.Passport] = 90,
            [ReminderLeadTime.MatterKeyword.Licence] = 60,
            [ReminderLeadTime.MatterKeyword.Registration] = 60,
            // Mostly comparing quotes before committing.
            [ReminderLeadTime.MatterKeyword.Insurance] = 45,
            // The longest thing on the list, and the one people most underestimate.
            [ReminderLeadTime.MatterKeyword.Tax] = 120,
            // Usually a few taps in an account page.
            [ReminderLeadTime.MatterKeyword.Subscription] = 15,
            // Paying something is quick; it is REMEMBERING that is hard, which is
            // why this row earns a short duration and a generous lead time.
            [ReminderLeadTime.MatterKeyword.Bill] = 10,
            // You attend it, so the duration is the appointment itself.
            [ReminderLeadTime.MatterKeyword.Appointment] = 60,
        };

    private static readonly IReadOnlyDictionary<string, int> DomainMinutes =
        new Dictionary<string, int>
        {
            ["health"] = 60,
            ["home"] = 45,
            ["car"] = 60,
            ["finance"] = 15,
            ["family"] = 45,
            ["pets"] = 30,
        };

    /// <summary>
    /// The minutes to reserve for a matter.
    ///
    /// <para>
    /// <b>The upper bound is used, not the lower.</b> An estimate is a range, and the
    /// number wanted here is "when is it too late to start" — for which the generous
    /// end is the only safe one. It is the same reasoning
    /// <see cref="EstimateNormalizer.Snap"/> already applies when it rounds ties up:
    /// people underestimate how long admin takes.
    /// </para>
    ///
    /// <para>
    /// A stored estimate always wins, whichever source it came from. An
    /// AI-produced one is a judgement about THIS matter; the tables below are
    /// judgements about matters shaped like it, which is strictly less information.
    /// </para>
    /// </summary>
    public static int ResolveMinutes(string title, string domain, TaskEstimateDocument? estimate)
    {
        if (estimate is not null && estimate.MaxMinutes > 0)
        {
            return estimate.MaxMinutes;
        }

        var keyword = ReminderLeadTime.MatchKeyword(title);
        if (keyword is { } key && KeywordMinutes.TryGetValue(key, out var minutes))
        {
            return EstimateNormalizer.Snap(minutes);
        }

        return EstimateNormalizer.Snap(
            DomainMinutes.TryGetValue(domain, out var byDomain) ? byDomain : FallbackMinutes);
    }

    /// <summary>Convenience over a whole matter.</summary>
    public static int ResolveMinutes(TaskDocument task) =>
        ResolveMinutes(task.Title, task.Domain, task.Estimate);
}
