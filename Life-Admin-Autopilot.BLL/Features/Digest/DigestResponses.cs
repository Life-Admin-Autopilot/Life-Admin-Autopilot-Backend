using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.Digest;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary><c>GET /me/digest</c> — <c>{ "digest": { … } }</c>.</summary>
public sealed class DigestResponse
{
    [JsonPropertyName("digest")]
    public required DailyDigestDto Digest { get; init; }
}

/// <summary>
/// The digest body. Mirrors <c>DailyDigestPayload</c> in
/// <c>server/src/models/DailyDigest.ts</c>, which the client type in
/// <c>queries/digest.ts</c> also mirrors — change one, change all three.
/// </summary>
public sealed class DailyDigestDto
{
    /// <summary>Local calendar date this digest describes, <c>YYYY-MM-DD</c>.</summary>
    [JsonPropertyName("localDate")]
    public required string LocalDate { get; init; }

    /// <summary>
    /// The instant the digest was COMPUTED — not the instant of this request.
    /// A cache hit replays the value stored by the build that produced it, so
    /// successive calls return an identical, and increasingly stale, timestamp.
    /// A plain string end to end: it is stored as one, so it never round-trips
    /// through a date formatter that could re-render it differently.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public required string GeneratedAt { get; init; }

    [JsonPropertyName("headline")]
    public required string Headline { get; init; }

    /// <summary>
    /// A model is writing a better <see cref="Headline"/> for this day right now.
    ///
    /// <para>
    /// The dashboard refetches while this is true and stops the moment it is false —
    /// so it is an assertion that a write is actually in flight, not that one would
    /// be nice. False on a server with no model configured, on a day with nothing to
    /// describe, and the instant the attempt finishes whether it produced a sentence
    /// or not.
    /// </para>
    ///
    /// <para>
    /// NOT part of the stored payload: it describes this instant, not the day. A
    /// client that ignores it simply sees the improved sentence on its next visit.
    /// </para>
    /// </summary>
    [JsonPropertyName("prosePending")]
    public required bool ProsePending { get; init; }

    [JsonPropertyName("counts")]
    public required DailyDigestCountsDto Counts { get; init; }

    [JsonPropertyName("estimatedMinutesToday")]
    public required DailyDigestEstimateDto EstimatedMinutesToday { get; init; }

    [JsonPropertyName("themes")]
    public required IReadOnlyList<DailyDigestThemeDto> Themes { get; init; }

    /// <summary>Explicit <c>null</c> when nothing is due in the week ahead.</summary>
    [JsonPropertyName("busiestDay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DailyDigestBusiestDayDto? BusiestDay { get; init; }

    [JsonPropertyName("duplicates")]
    public required IReadOnlyList<DailyDigestDuplicateDto> Duplicates { get; init; }
}

public sealed class DailyDigestCountsDto
{
    [JsonPropertyName("dueToday")]
    public required int DueToday { get; init; }

    [JsonPropertyName("completedToday")]
    public required int CompletedToday { get; init; }

    [JsonPropertyName("openTotal")]
    public required int OpenTotal { get; init; }

    [JsonPropertyName("slipping")]
    public required int Slipping { get; init; }

    [JsonPropertyName("needsInput")]
    public required int NeedsInput { get; init; }

    [JsonPropertyName("scansAwaitingReview")]
    public required int ScansAwaitingReview { get; init; }
}

public sealed class DailyDigestEstimateDto
{
    [JsonPropertyName("min")]
    public required double Min { get; init; }

    [JsonPropertyName("max")]
    public required double Max { get; init; }
}

public sealed class DailyDigestThemeDto
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("taskIds")]
    public required IReadOnlyList<string> TaskIds { get; init; }
}

public sealed class DailyDigestBusiestDayDto
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

public sealed class DailyDigestDuplicateDto
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("taskIds")]
    public required IReadOnlyList<string> TaskIds { get; init; }
}

/// <summary>
/// Stored row to response body.
///
/// <para>
/// Written out field by field rather than by projection or spread, exactly as
/// Node's <c>fromCache</c> is — so a field added to the stored schema without a
/// matching change here surfaces as a compile error instead of silently vanishing
/// from the response.
/// </para>
/// </summary>
public static class DigestMappers
{
    /// <summary>
    /// Takes the RESULT rather than the payload, because <c>prosePending</c> is the
    /// one response field with no stored counterpart — passing it separately would
    /// let a caller forget it, and it defaults to the value that makes the client
    /// stop polling.
    /// </summary>
    public static DailyDigestDto ToDto(this DailyDigestResult result) =>
        result.Payload.ToDto(result.ProsePending);

    public static DailyDigestDto ToDto(this DailyDigestPayloadDocument payload, bool prosePending) => new()
    {
        LocalDate = payload.LocalDate,
        GeneratedAt = payload.GeneratedAt,
        Headline = payload.Headline,
        ProsePending = prosePending,
        Counts = new DailyDigestCountsDto
        {
            DueToday = payload.Counts.DueToday,
            CompletedToday = payload.Counts.CompletedToday,
            OpenTotal = payload.Counts.OpenTotal,
            Slipping = payload.Counts.Slipping,
            NeedsInput = payload.Counts.NeedsInput,
            ScansAwaitingReview = payload.Counts.ScansAwaitingReview,
        },
        EstimatedMinutesToday = new DailyDigestEstimateDto
        {
            Min = payload.EstimatedMinutesToday.Min,
            Max = payload.EstimatedMinutesToday.Max,
        },
        Themes = payload.Themes
            .Select(t => new DailyDigestThemeDto
            {
                Label = t.Label,
                Count = t.Count,
                TaskIds = t.TaskIds.ToList(),
            })
            .ToList(),
        BusiestDay = payload.BusiestDay is null
            ? null
            : new DailyDigestBusiestDayDto
            {
                Date = payload.BusiestDay.Date,
                Count = payload.BusiestDay.Count,
            },
        Duplicates = payload.Duplicates
            .Select(d => new DailyDigestDuplicateDto
            {
                Title = d.Title,
                Count = d.Count,
                TaskIds = d.TaskIds.ToList(),
            })
            .ToList(),
    };
}
