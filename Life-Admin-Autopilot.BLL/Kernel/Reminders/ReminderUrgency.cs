using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Kernel.Reminders;

/// <summary>
/// How loud a reminder is, relative to the others competing with it.
///
/// <para>
/// <see cref="ReminderLeadTime"/> decides <b>when</b> a reminder fires. This decides
/// <b>which one the user sees first</b> when several fire together — the gap
/// <c>docs/smart-reminder-conflict-spec.md</c> §3.1 names: priority is captured from
/// the user's own words at every entry point and then, in the reminder path, read by
/// nothing at all.
/// </para>
///
/// <para>
/// <b>It changes no schedule.</b> Nothing here can move, add, drop or delay a
/// reminder — the lead-time table remains the sole authority on timing, and this is
/// a pure ordering key computed on read. That is the whole of Phase 1.
/// </para>
///
/// <para>
/// Pure and static on purpose: ordering that only exists inside a Mongo-dependent
/// worker tick can only be tested against a live database, and an ordering rule is
/// exactly the kind of thing that must stay pinned.
/// </para>
/// </summary>
public static class ReminderUrgency
{
    /// <summary>
    /// The spec's formula is <c>f(priority_weight, time_remaining, task_domain)</c>.
    /// Priority is the integer part and deadline pressure the fraction, so the score
    /// reads directly: <c>2.75</c> is a <c>high</c> matter three quarters of the way
    /// through its warning window.
    /// </summary>
    public const double MaxScore = 4.0;

    /// <summary>
    /// Deadline pressure: <c>0</c> at the moment the heads-up fires, <c>1</c> at the
    /// deadline itself.
    ///
    /// <para>
    /// <b>Measured against the task's OWN warning window, not a fixed span.</b> A
    /// passport renewal is given 180 days of warning and a dentist appointment one,
    /// so "two days out" is nearly idle for the first and long past urgent for the
    /// second. Normalising by <see cref="ReminderLeadTime.ComputeLeadDays"/> is also
    /// how <c>task_domain</c> genuinely enters the formula — through the table that
    /// already encodes how much warning each kind of matter deserves — rather than
    /// through an invented ranking of domains against each other, which would be a
    /// value judgement we cannot defend (is <c>health</c> above <c>finance</c>?).
    /// </para>
    ///
    /// <para>
    /// A matter with no <c>dueAt</c> has no deadline to press against and scores
    /// <c>0</c> pressure — it is ranked on the user's stated priority alone.
    /// Overdue clamps at <c>1</c> rather than running away: a reminder the worker
    /// was late to fire should sort to the top, not above every possible future one.
    /// </para>
    /// </summary>
    public static double Pressure(ReminderTaskShape task, DateTime at)
    {
        if (task.DueAt is not { } due)
        {
            return 0;
        }

        var window = ReminderLeadTime.ComputeLeadDays(task);
        if (window <= 0)
        {
            // Unreachable through ComputeLeadDays (its floor is 1), but a zero window
            // would divide by zero and silently produce Infinity, so it is spelled out.
            return 1;
        }

        var remainingDays = (due - at).TotalDays;
        var elapsed = 1 - (remainingDays / window);

        return Math.Clamp(elapsed, 0, 1);
    }

    /// <summary>
    /// The combined score, in <c>[0, <see cref="MaxScore"/>]</c>.
    ///
    /// <para>
    /// <b>Priority dominates, pressure orders within it.</b> An <c>urgent</c> matter
    /// outranks a <c>low</c> one that is closer to its deadline, because priority is
    /// the user's own statement of what matters and pressure is our inference about
    /// it. The two are deliberately NOT weighted against each other — the integer
    /// part is the user's word, the fraction is ours. Changing that balance is a
    /// one-line change here with tests that will tell you exactly what moved.
    /// </para>
    ///
    /// <para>
    /// Equal scores are possible at the boundaries (a <c>low</c> matter at its
    /// deadline scores exactly <c>1.0</c>, as does a <c>normal</c> one whose warning
    /// window has only just opened). Ties are broken by time, not left to chance —
    /// see <see cref="DeliveryOrder{T}"/>.
    /// </para>
    ///
    /// <para>Rounded to three decimals so the value serialises identically on every
    /// run and two scores can be compared exactly.</para>
    /// </summary>
    /// <param name="task">Title, domain and kind feed the warning-window lookup.</param>
    /// <param name="priority">The task's priority; an unknown value falls back to
    /// <c>normal</c> exactly as <see cref="TaskVocabulary.RankFor"/> does.</param>
    /// <param name="at">The reminder's own instant — NOT "now". A task's heads-up and
    /// its at-deadline nudge score differently, which is the point.</param>
    public static double Score(ReminderTaskShape task, string? priority, DateTime at) =>
        Math.Round(TaskVocabulary.RankFor(priority) + Pressure(task, at), 3);

    /// <summary>
    /// The order to WRITE a batch of fired reminders in, least urgent first.
    ///
    /// <para>
    /// <b>Reversed on purpose, and it is not a bug.</b> Both surfaces that show these
    /// rows put the newest at the top — the in-app feed sorts <c>{createdAt: -1}</c>
    /// and the OS notification tray stacks newest-first — so the row written LAST is
    /// the row the user sees FIRST. Writing in ascending urgency is therefore what
    /// puts the most urgent matter on top. Sorting this the "right" way round is the
    /// obvious change to make here and it inverts the feature.
    /// </para>
    ///
    /// <para>
    /// Ties break on the reminder instant, so the sooner deadline wins the higher
    /// slot. Without it, equal scores would fall back to whatever order Mongo
    /// happened to return — which is what this whole class exists to replace.
    /// </para>
    /// </summary>
    public static IReadOnlyList<T> DeliveryOrder<T>(
        IEnumerable<T> items,
        Func<T, double> score,
        Func<T, DateTime> at) =>
        items
            .OrderBy(score)
            // Descending, because this list is reversed relative to what is displayed:
            // the LATEST instant is written first so the EARLIEST ends up on top.
            .ThenByDescending(at)
            .ToList();
}
