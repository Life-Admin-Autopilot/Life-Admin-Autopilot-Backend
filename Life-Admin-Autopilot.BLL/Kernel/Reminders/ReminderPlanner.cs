using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Kernel.Reminders;

/// <summary>
/// Optional AI refinement of a reminder schedule.
///
/// <para>
/// The kernel ships <see cref="NullReminderRefiner"/>, which reports "not
/// configured" — the exact behaviour of Node with no <c>GEMINI_API_KEY</c>, and
/// therefore the parity target. The AI slice replaces the registration with a
/// real implementation via <c>services.AddSingleton&lt;IReminderRefiner, …&gt;()</c>.
/// </para>
/// </summary>
public interface IReminderRefiner
{
    bool IsConfigured { get; }

    /// <summary>Candidate instants, unfiltered. The planner clamps and dedupes them.</summary>
    Task<IReadOnlyList<DateTime>> SuggestAsync(TaskDocument task, DateTime now, CancellationToken cancellationToken = default);
}

public sealed class NullReminderRefiner : IReminderRefiner
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<DateTime>> SuggestAsync(
        TaskDocument task,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DateTime>>(Array.Empty<DateTime>());
}

/// <summary>
/// Port of <c>server/src/modules/reminders/planReminders.ts</c> — writes the
/// reminder schedule onto a task.
///
/// <para>
/// <b>Call it ONLY when <c>dueAt</c> or <c>kind</c> changes.</b> It overwrites
/// <c>reminders</c> fresh, clearing <c>firedAt</c>, so calling it on an unrelated
/// edit re-arms reminders that already went out.
/// </para>
///
/// <para><b>It never throws.</b> A scheduling hiccup must not fail the task
/// mutation that triggered it — every path logs and returns.</para>
/// </summary>
public sealed class ReminderPlanner
{
    /// <summary>
    /// Only spend an AI call when the deadline is far enough out that smart
    /// lead-time judgment actually matters; near-term reminders use the rules floor
    /// alone.
    /// </summary>
    private static readonly TimeSpan AiRefineMinHorizon = TimeSpan.FromDays(2);

    private readonly IMongoCollection<TaskDocument> _tasks;
    private readonly IReminderRefiner _refiner;
    private readonly ILogger<ReminderPlanner> _logger;

    public ReminderPlanner(IMongoDatabase database, IReminderRefiner refiner, ILogger<ReminderPlanner> logger)
    {
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
        _refiner = refiner;
        _logger = logger;
    }

    /// <summary>Write the deterministic (rules-table) schedule, then optionally kick off AI refinement.</summary>
    public async Task SetRulesRemindersAsync(
        TaskDocument task,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var at = now ?? DateTime.UtcNow;
        try
        {
            var entries = ReminderLeadTime
                .ComputeRules(new ReminderTaskShape(task.Title, task.Domain, task.Kind, task.DueAt), at)
                .Select(e => new ReminderEntryDocument { At = e.At, Kind = e.Kind })
                .ToList();

            task.Reminders = entries;
            await _tasks
                .UpdateOneAsync(
                    Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
                    Builders<TaskDocument>.Update.Set(t => t.Reminders, entries),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Hybrid: refine far-out schedules with AI. Fire-and-forget — the rules
            // floor above stands if AI is off / over quota / fails.
            if (task.DueAt.HasValue && task.DueAt.Value - at > AiRefineMinHorizon)
            {
                _ = RefineWithAiAsync(task.Id, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "reminder:schedule-failed taskId={TaskId}", task.Id);
        }
    }

    /// <summary>
    /// A snoozed task fires ONCE, at the snooze moment — that is the point of
    /// snooze.
    /// </summary>
    public async Task SetSnoozeReminderAsync(TaskDocument task, CancellationToken cancellationToken = default)
    {
        try
        {
            if (task.SnoozedUntil is null)
            {
                return;
            }

            var entries = new List<ReminderEntryDocument>
            {
                new() { At = task.SnoozedUntil.Value, Kind = "due" },
            };

            task.Reminders = entries;
            await _tasks
                .UpdateOneAsync(
                    Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
                    Builders<TaskDocument>.Update.Set(t => t.Reminders, entries),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "reminder:snooze-schedule-failed taskId={TaskId}", task.Id);
        }
    }

    /// <summary>
    /// Re-reads the task so it never clobbers a reminder that already FIRED between
    /// scheduling and refinement.
    /// </summary>
    public async Task RefineWithAiAsync(ObjectId taskId, CancellationToken cancellationToken = default)
    {
        if (!_refiner.IsConfigured)
        {
            return;
        }

        var now = DateTime.UtcNow;
        try
        {
            var task = await _tasks
                .Find(Builders<TaskDocument>.Filter.Eq(t => t.Id, taskId))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (task is null || task.Kind != "reminder" || task.DueAt is null)
            {
                return;
            }

            if (task.Reminders.Any(r => r.FiredAt is not null))
            {
                return;
            }

            var due = task.DueAt.Value;
            var suggestions = await _refiner.SuggestAsync(task, now, cancellationToken).ConfigureAwait(false);
            var valid = suggestions.Where(d => d > now && d <= due).ToList();

            // Keep the rules floor rather than replacing it with nothing.
            if (valid.Count == 0)
            {
                return;
            }

            var entries = valid.Select(at => new ReminderEntryDocument { At = at, Kind = "ai" }).ToList();

            // Always keep a nudge at the deadline itself.
            if (!valid.Any(d => Math.Abs((d - due).TotalMilliseconds) < 60_000))
            {
                entries.Add(new ReminderEntryDocument { At = due, Kind = "due" });
            }

            // Dedupe by minute, sorted.
            var seen = new HashSet<long>();
            var deduped = entries
                .OrderBy(e => e.At)
                .Where(e => seen.Add(e.At.Ticks / TimeSpan.TicksPerMinute))
                .ToList();

            await _tasks
                .UpdateOneAsync(
                    Builders<TaskDocument>.Filter.Eq(t => t.Id, taskId),
                    Builders<TaskDocument>.Update.Set(t => t.Reminders, deduped),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("reminder:ai-refined taskId={TaskId} count={Count}", taskId, deduped.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "reminder:ai-refine-failed taskId={TaskId}", taskId);
        }
    }
}
