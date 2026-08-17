using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Aggregated read-only financial insights representation.
/// Contains counts and lists of open tasks in the finance domain.
/// </summary>
public sealed class FinancialInsightsDto
{
    [JsonPropertyName("overdueCount")]
    public int OverdueCount { get; init; }

    [JsonPropertyName("nearTermCount")]
    public int NearTermCount { get; init; }

    [JsonPropertyName("undatedCount")]
    public int UndatedCount { get; init; }

    [JsonPropertyName("urgentCount")]
    public int UrgentCount { get; init; }

    [JsonPropertyName("overdueTasks")]
    public IReadOnlyList<TaskDto> OverdueTasks { get; init; } = Array.Empty<TaskDto>();

    [JsonPropertyName("nearTermTasks")]
    public IReadOnlyList<TaskDto> NearTermTasks { get; init; } = Array.Empty<TaskDto>();

    [JsonPropertyName("undatedTasks")]
    public IReadOnlyList<TaskDto> UndatedTasks { get; init; } = Array.Empty<TaskDto>();

    [JsonPropertyName("urgentTasks")]
    public IReadOnlyList<TaskDto> UrgentTasks { get; init; } = Array.Empty<TaskDto>();
}
