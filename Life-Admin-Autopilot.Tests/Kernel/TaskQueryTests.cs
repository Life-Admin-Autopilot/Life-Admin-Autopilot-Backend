using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The filter builder, the cursor and the day boundaries — the parts seven
/// modules share and therefore must not drift.
/// </summary>
public sealed class TaskQueryTests
{
    private static readonly ObjectId UserId = ObjectId.Parse("6a78c216aa461ae1dc64ab59");

    private static readonly DateTime Now = new(2026, 8, 9, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void every_filter_is_user_scoped_and_excludes_soft_deleted_rows()
    {
        // Act
        var filter = TaskQuery.BuildFilter(UserId, new TaskQuery.TaskFilter(), Now);

        // Assert — $exists:false, NOT {deletedAt: null}: Mongoose omits unset fields
        // and the index depends on the operator.
        Assert.Equal(UserId, filter["userId"].AsObjectId);
        Assert.Equal(new BsonDocument("$exists", false), filter["deletedAt"].AsBsonDocument);
    }

    [Fact]
    public void overdue_narrows_status_to_open_and_snoozed_when_none_was_given()
    {
        // Act
        var filter = TaskQuery.BuildFilter(UserId, new TaskQuery.TaskFilter { Overdue = true }, Now);

        // Assert — something already done isn't overdue, however stale its date.
        Assert.Equal(
            new BsonArray(new[] { "open", "snoozed" }),
            filter["status"].AsBsonDocument["$in"].AsBsonArray);
        Assert.Equal(Now, filter["dueAt"].AsBsonDocument["$lt"].ToUniversalTime());
    }

    [Fact]
    public void overdue_keeps_an_explicit_status_filter()
    {
        // Act
        var filter = TaskQuery.BuildFilter(
            UserId,
            new TaskQuery.TaskFilter { Overdue = true, Status = new[] { "done" } },
            Now);

        // Assert
        Assert.Equal(new BsonArray(new[] { "done" }), filter["status"].AsBsonDocument["$in"].AsBsonArray);
    }

    [Fact]
    public void undated_wins_over_an_explicit_due_range()
    {
        // Act
        var filter = TaskQuery.BuildFilter(
            UserId,
            new TaskQuery.TaskFilter { Undated = true, DueBefore = Now },
            Now);

        // Assert
        Assert.Equal(new BsonDocument("$exists", false), filter["dueAt"].AsBsonDocument);
    }

    [Fact]
    public void untagged_overwrites_a_tag_filter()
    {
        // Act — assignment order in Node puts `untagged` after `tag`.
        var filter = TaskQuery.BuildFilter(
            UserId,
            new TaskQuery.TaskFilter { Tag = "travel", Untagged = true },
            Now);

        // Assert
        Assert.Equal(new BsonDocument("$size", 0), filter["tags"].AsBsonDocument);
    }

    [Fact]
    public void tags_are_normalized_before_matching()
    {
        // Act
        var filter = TaskQuery.BuildFilter(UserId, new TaskQuery.TaskFilter { Tag = " Big Trip ,ADMIN" }, Now);

        // Assert
        Assert.Equal(
            new BsonArray(new[] { "big-trip", "admin" }),
            filter["tags"].AsBsonDocument["$in"].AsBsonArray);
    }

    [Fact]
    public void free_text_is_regex_escaped_across_title_and_notes()
    {
        // Act — a stray '(' would otherwise be a 500 or a backtracking hazard.
        var filter = TaskQuery.BuildFilter(UserId, new TaskQuery.TaskFilter { Q = "a(b" }, Now);

        // Assert
        var or = filter["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal(@"a\(b", or[0].AsBsonDocument["title"].AsBsonRegularExpression.Pattern);
        Assert.Equal("i", or[0].AsBsonDocument["title"].AsBsonRegularExpression.Options);
    }

    [Fact]
    public void cursor_round_trips_as_base64url()
    {
        // Act
        var encoded = TaskQuery.EncodeCursor(50);

        // Assert — base64url: no padding, no '+' or '/'.
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(50, TaskQuery.DecodeCursor(encoded));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not base64!!!")]
    [InlineData("LTU=")]
    public void an_unusable_cursor_decodes_to_zero(string? cursor)
    {
        // Assert — a stale token must never 500.
        Assert.Equal(0, TaskQuery.DecodeCursor(cursor));
    }

    [Fact]
    public void day_boundaries_are_computed_in_the_callers_zone()
    {
        // Arrange — 18:00 UTC on Aug 9 is 20:00 in Cairo (UTC+2 in summer), so the
        // local day started at 22:00 UTC on Aug 8.
        var boundaries = TaskQuery.GetDayBoundaries(Now, "Africa/Cairo");

        // Assert
        Assert.Equal(new DateTime(2026, 8, 8, 21, 0, 0, DateTimeKind.Utc), boundaries.TodayStart);
        Assert.Equal(boundaries.TodayStart.AddDays(1), boundaries.TomorrowStart);
        Assert.Equal(boundaries.TodayStart.AddDays(2), boundaries.DayAfterTomorrowStart);

        // "This week" is the next SEVEN DAYS, not the calendar week.
        Assert.Equal(boundaries.TodayStart.AddDays(7), boundaries.WeekEnd);
    }

    [Fact]
    public void a_missing_zone_falls_back_to_utc()
    {
        // Assert
        Assert.Equal(0, TaskQuery.ZoneOffsetMinutes(Now, null));
        Assert.Equal(new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc), TaskQuery.StartOfLocalDay(Now, null));
    }

    [Fact]
    public void an_unrecognised_zone_throws_rather_than_silently_using_utc()
    {
        // Assert — Node's Intl call raises here too, surfacing as a 500. A silent UTC
        // fallback would move a Cairo user's whole day with nothing to reveal it.
        Assert.ThrowsAny<Exception>(() => TaskQuery.ZoneOffsetMinutes(Now, "Not/AZone"));
    }

    [Fact]
    public void due_sort_pushes_dateless_tasks_to_the_end_in_both_directions()
    {
        // Assert — Mongo orders missing fields first ascending, which would bury a
        // user's dated work under an undated backlog.
        Assert.True(TaskQuery.FarFuture > new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(TaskQuery.FarPast < new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, TaskQuery.SortStage("due-asc")["_dueSort"].AsInt32);
        Assert.Equal(-1, TaskQuery.SortStage("due-desc")["_dueSort"].AsInt32);
    }
}
