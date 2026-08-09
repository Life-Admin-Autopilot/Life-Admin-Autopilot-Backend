using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Bucket counts for the Matters header, the filter badges and the dashboard hero.
///
/// <para>
/// The buckets are deliberately the same ones the list groups by, computed from
/// the same day boundaries, so a section header reading "TODAY 3" can never
/// disagree with the badge above it.
/// </para>
/// </summary>
public sealed class TaskCountsDto
{
    // ---- Live buckets. Open/snoozed only; mutually exclusive and exhaustive. ----

    [JsonPropertyName("overdue")]
    public int Overdue { get; init; }

    [JsonPropertyName("today")]
    public int Today { get; init; }

    [JsonPropertyName("tomorrow")]
    public int Tomorrow { get; init; }

    [JsonPropertyName("thisWeek")]
    public int ThisWeek { get; init; }

    [JsonPropertyName("later")]
    public int Later { get; init; }

    [JsonPropertyName("undated")]
    public int Undated { get; init; }

    // ---- Cross-cutting ----

    [JsonPropertyName("open")]
    public int Open { get; init; }

    [JsonPropertyName("done")]
    public int Done { get; init; }

    [JsonPropertyName("trashed")]
    public int Trashed { get; init; }

    /// <summary>
    /// "Needs a look" — the number the triage banner shows. Bounded and recent by
    /// construction: an unbounded lifetime overdue count is what drives people to
    /// delete the app.
    /// </summary>
    [JsonPropertyName("slipping")]
    public int Slipping { get; init; }

    /// <summary>
    /// Finished inside the caller's LOCAL day — the numerator of the day-progress
    /// pill. Distinct from <see cref="Done"/>, which is every matter ever completed.
    /// </summary>
    [JsonPropertyName("completedToday")]
    public int CompletedToday { get; init; }

    /// <summary>
    /// The two other "needs you" inboxes, so the dashboard strip resolves from ONE
    /// request instead of racing a fast query against a slow one.
    /// </summary>
    [JsonPropertyName("needsInput")]
    public int NeedsInput { get; init; }

    [JsonPropertyName("scansAwaitingReview")]
    public int ScansAwaitingReview { get; init; }

    [JsonPropertyName("byDomain")]
    public IReadOnlyDictionary<string, int> ByDomain { get; init; } = new Dictionary<string, int>();

    [JsonPropertyName("byPriority")]
    public IReadOnlyDictionary<string, int> ByPriority { get; init; } = new Dictionary<string, int>();
}
