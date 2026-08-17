using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Service coordinating aggregation logic for the read-only Financial Insights feature.
/// Retrieves open finance tasks and partitions them into overdue, near-term, undated, and urgent categories.
/// </summary>
public sealed class FinancialInsightsService
{
    private readonly IMongoCollection<TaskDocument> _tasks;
    private readonly UserLocaleReader _locales;

    public FinancialInsightsService(IMongoDatabase database, UserLocaleReader locales)
    {
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
        _locales = locales;
    }

    public async Task<FinancialInsightsDto> ComputeAsync(
        ObjectId userId,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        // Query open/snoozed tasks in the finance domain for this user
        var filter = Builders<TaskDocument>.Filter.And(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Filter.Exists("deletedAt", false),
            Builders<TaskDocument>.Filter.Eq(t => t.Domain, "finance"),
            Builders<TaskDocument>.Filter.In(t => t.Status, new[] { "open", "snoozed" })
        );

        var tasks = await _tasks
            .Find(filter)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Filter and sort overdue tasks (Due date has passed)
        var overdue = tasks
            .Where(t => t.DueAt.HasValue && t.DueAt.Value < now)
            .OrderBy(t => t.DueAt!.Value)
            .ThenByDescending(t => TaskVocabulary.RankFor(t.Priority))
            .ToList();

        // Filter and sort near-term tasks (Due within the next 14 days)
        var nearTerm = tasks
            .Where(t => t.DueAt.HasValue && t.DueAt.Value >= now && t.DueAt.Value <= now.AddDays(14))
            .OrderBy(t => t.DueAt!.Value)
            .ThenByDescending(t => TaskVocabulary.RankFor(t.Priority))
            .ToList();

        // Filter and sort undated tasks (No due date set)
        var undated = tasks
            .Where(t => !t.DueAt.HasValue)
            .OrderByDescending(t => TaskVocabulary.RankFor(t.Priority))
            .ThenByDescending(t => t.CreatedAt)
            .ToList();

        // Filter and sort urgent tasks (High or urgent priority, independent of due date)
        var urgent = tasks
            .Where(t => t.Priority == "high" || t.Priority == "urgent")
            .OrderBy(t => t.DueAt.HasValue ? 0 : 1) // Null due dates last
            .ThenBy(t => t.DueAt)
            .ThenByDescending(t => TaskVocabulary.RankFor(t.Priority))
            .ToList();

        // Apply locale presentation overlay
        var locale = await _locales.ReadAsync(userId, cancellationToken).ConfigureAwait(false);

        var overdueDtos = MatterLocale.PresentMany(overdue, locale);
        var nearTermDtos = MatterLocale.PresentMany(nearTerm, locale);
        var undatedDtos = MatterLocale.PresentMany(undated, locale);
        var urgentDtos = MatterLocale.PresentMany(urgent, locale);

        return new FinancialInsightsDto
        {
            OverdueCount = overdueDtos.Count,
            NearTermCount = nearTermDtos.Count,
            UndatedCount = undatedDtos.Count,
            UrgentCount = urgentDtos.Count,

            OverdueTasks = overdueDtos,
            NearTermTasks = nearTermDtos,
            UndatedTasks = undatedDtos,
            UrgentTasks = urgentDtos
        };
    }
}
