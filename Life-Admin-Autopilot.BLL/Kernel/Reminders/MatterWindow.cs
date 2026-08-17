using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Kernel.Reminders;

/// <summary>
/// The span of time a matter actually occupies, and whether two of them collide —
/// <c>docs/smart-reminder-conflict-spec.md</c> §3.3.
///
/// <para>
/// <b>A conflict is not "the dates are close".</b> That was the old rule: a fixed
/// two-hour radius around <c>dueAt</c>, identical for a ten-minute bill and a
/// four-hour tax return. It is wrong in both directions at once — it flags two quick
/// errands ninety minutes apart, which do not collide, and it misses two four-hour
/// jobs three hours apart, which certainly do.
/// </para>
///
/// <para>
/// A matter runs from the last moment it can still be started to its deadline, which
/// is the same interval Phase 2 already schedules the final nudge against. Two
/// matters conflict when those intervals overlap once each has been given a little
/// breathing room.
/// </para>
///
/// <para>
/// Pure and static, deliberately: the old rule lived inside a Mongo-backed service
/// with four callers and no test of any kind, so the arithmetic that decides what a
/// conflict IS could only be exercised against a live database. It can now be read
/// and pinned on its own.
/// </para>
/// </summary>
public static class MatterWindow
{
    /// <summary>
    /// The gap two matters need between them before they count as comfortable.
    ///
    /// <para>
    /// Touching intervals are not really fine: nothing accounts for travel, for
    /// finding the right document, or for the minutes a person needs between one
    /// thing and the next. Fifteen is deliberately fixed rather than scaled to the
    /// longer matter — a user has to be able to predict what will and will not be
    /// flagged, and a boundary that moves per pair cannot be explained in a sentence.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Buffer = TimeSpan.FromMinutes(15);

    /// <summary>One matter's occupied span: <c>[deadline - duration, deadline]</c>.</summary>
    public readonly record struct Span(DateTime Start, DateTime End)
    {
        public TimeSpan Length => End - Start;
    }

    /// <summary>
    /// The span a matter occupies, from whatever is known about it — its own estimate
    /// when it has one, otherwise the duration table.
    /// </summary>
    public static Span For(string title, string domain, TaskEstimateDocument? estimate, DateTime dueAt)
    {
        var minutes = ReminderDuration.ResolveMinutes(title, domain, estimate);
        return new Span(dueAt.AddMinutes(-minutes), dueAt);
    }

    /// <summary>The span of an existing matter. Returns null for one with no deadline.</summary>
    public static Span? For(TaskDocument task) =>
        task.DueAt is { } due ? For(task.Title, task.Domain, task.Estimate, due) : null;

    /// <summary>
    /// Do these two collide once each is given <see cref="Buffer"/> of breathing room?
    ///
    /// <para>
    /// Only ONE buffer is applied, to one side, rather than padding both spans: two
    /// matters need fifteen minutes BETWEEN them, not fifteen from each, and padding
    /// both would silently demand thirty.
    /// </para>
    /// </summary>
    public static bool Overlap(Span a, Span b) => Overlap(a, b, Buffer);

    /// <summary>The same test with an explicit gap, so a caller can ask for none.</summary>
    public static bool Overlap(Span a, Span b, TimeSpan buffer) =>
        a.Start - buffer < b.End && b.Start - buffer < a.End;

    /// <summary>
    /// How far a clashing matter is offered to move.
    ///
    /// <para>
    /// A coarse affordance for the "Later that day" option on a clarification card,
    /// NOT the detection rule — it used to be derived from the old fixed radius, and
    /// keeping the offered jump the same size is what stops that card changing
    /// behaviour for a reason unrelated to it.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SuggestedShift = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30);
}
