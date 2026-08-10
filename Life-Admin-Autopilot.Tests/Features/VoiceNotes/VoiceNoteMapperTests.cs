using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.VoiceNotes;

/// <summary>
/// The <c>toJSON</c> transform. These tests are about what is NOT in the payload
/// as much as what is — eight fields are stripped, and two of them matter beyond
/// tidiness: <c>storageKey</c> points at the user's recording, and
/// <c>clarifyItems</c> is a staging lane that the client is supposed to see only
/// through the Clarifications endpoint.
/// </summary>
public sealed class VoiceNoteMapperTests
{
    /// <summary>Every field the Mongoose transform deletes.</summary>
    private static readonly string[] StrippedFields =
    {
        "storageKey", "attempts", "maxAttempts", "lockedUntil",
        "nextRunAt", "lastError", "notifiedAt", "clarifyItems",
    };

    [Fact]
    public void strips_all_eight_internal_fields()
    {
        // Arrange — every stripped field populated, so a leak cannot hide behind a null.
        var note = Populated();

        // Act
        var json = Serialize(note);

        // Assert
        foreach (var field in StrippedFields)
        {
            Assert.False(json.TryGetProperty(field, out _), $"'{field}' must never reach a client.");
        }
    }

    [Fact]
    public void strips_clarifyItems_even_when_the_lane_is_populated()
    {
        // The one deletion that is not job machinery. A staged question surfaces to
        // the client as a Clarification on its own endpoint, never here — and the
        // lane carries the user's own words in `title`/`question`.
        var note = Populated();
        note.ClarifyItems.Add(new VoiceClarifyItemDocument
        {
            Key = "k1",
            Title = "Pay the water bill",
            Domain = "finance",
            Question = "Which Tuesday did you mean?",
        });

        var json = Serialize(note);

        Assert.False(json.TryGetProperty("clarifyItems", out _));
    }

    [Fact]
    public void keeps_failureReason_even_though_lastError_is_stripped()
    {
        // Arrange — the two hold the SAME string; only one is product state.
        var note = Populated();
        note.FailureReason = "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.";
        note.LastError = note.FailureReason;

        // Act
        var json = Serialize(note);

        // Assert
        Assert.Equal(note.FailureReason, json.GetProperty("failureReason").GetString());
        Assert.False(json.TryGetProperty("lastError", out _));
    }

    [Fact]
    public void emits_a_fresh_note_in_the_order_mongoose_first_set_the_fields()
    {
        // Captured from the live reference and frozen in the contract's 202 example.
        // NOT schema order: clientCapturedAt/timezone/mimeType come from the route's
        // create() literal, while extractedTasks/reviewItems arrive later as schema
        // DEFAULTS, so the literal's fields win the earlier slots even though the
        // schema declares the arrays first.
        var note = Fresh();

        var order = Serialize(note).EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "userId", "durationMs", "byteSize", "source", "status", "clientCapturedAt",
                "timezone", "mimeType", "extractedTasks", "reviewItems", "createdAt", "updatedAt", "id",
            },
            order);
    }

    [Fact]
    public void places_reviewedAt_after_updatedAt_and_before_id()
    {
        // Frozen in the contract's `observed_empty_body` example. reviewedAt is set
        // by the review commit, long after creation, and MongoDB appends a newly-set
        // field to the end of the stored document.
        var note = Fresh();
        note.ReviewedAt = new DateTime(2026, 8, 9, 17, 45, 17, 958, DateTimeKind.Utc);

        var order = Serialize(note).EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[] { "createdAt", "updatedAt", "reviewedAt", "id" },
            order[^4..]);
    }

    [Fact]
    public void omits_an_absent_timezone_and_mimeType_rather_than_emitting_null()
    {
        // Mongoose never stores an unset optional, so the key is simply absent. An
        // explicit null would be a different value to any client that checks
        // `'timezone' in note`.
        var note = Fresh();
        note.Timezone = null;
        note.MimeType = null;

        var json = Serialize(note);

        Assert.False(json.TryGetProperty("timezone", out _));
        Assert.False(json.TryGetProperty("mimeType", out _));
    }

    [Fact]
    public void renames_the_object_id_to_a_string_id()
    {
        var note = Fresh();

        var json = Serialize(note);

        Assert.Equal(note.Id.ToString(), json.GetProperty("id").GetString());
        Assert.False(json.TryGetProperty("_id", out _));
        Assert.False(json.TryGetProperty("__v", out _));
    }

    [Fact]
    public void carries_an_extracted_task_estimate_and_backlink_through_the_transform()
    {
        var note = Fresh();
        var taskId = ObjectId.GenerateNewId();
        note.ExtractedTasks.Add(new VoiceExtractedTaskDocument
        {
            Key = "abc",
            Title = "Book the MOT",
            Domain = "car",
            Priority = "high",
            Confidence = "high",
            ReviewReason = "clear",
            Estimate = new TaskEstimateDocument { MinMinutes = 10, MaxMinutes = 20, Source = "ai" },
            TaskId = taskId,
        });

        var item = Serialize(note).GetProperty("extractedTasks")[0];

        Assert.Equal("abc", item.GetProperty("key").GetString());
        Assert.Equal(taskId.ToString(), item.GetProperty("taskId").GetString());
        Assert.Equal(20, item.GetProperty("estimate").GetProperty("maxMinutes").GetInt32());

        // Absent, not null — the item has no due date and no notes.
        Assert.False(item.TryGetProperty("dueAt", out _));
        Assert.False(item.TryGetProperty("notes", out _));
    }

    [Fact]
    public void always_emits_the_reasons_array_on_a_review_item()
    {
        // `reasons` has a schema default of [], so it is REQUIRED in the contract —
        // absent would break a client that renders the "why am I seeing this" list.
        var note = Fresh();
        note.ReviewItems.Add(new VoiceReviewItemDocument
        {
            Key = "held",
            Title = "Something vague",
            Domain = "home",
        });

        var item = Serialize(note).GetProperty("reviewItems")[0];

        Assert.Empty(item.GetProperty("reasons").EnumerateArray());
    }

    private static VoiceNoteDocument Fresh() => new()
    {
        Id = ObjectId.Parse("6a78bc9caa461ae1dc64a294"),
        UserId = ObjectId.Parse("6a78bbbeaa461ae1dc64a0bf"),
        StorageKey = "6a78bbbeaa461ae1dc64a0bf/6a78bc9caa461ae1dc64a294.m4a",
        DurationMs = 4200,
        ByteSize = 2048,
        Source = "app",
        Status = "pending",
        ClientCapturedAt = new DateTime(2026, 8, 9, 10, 5, 0, DateTimeKind.Utc),
        Timezone = "Africa/Cairo",
        MimeType = "audio/m4a",
        NextRunAt = new DateTime(2026, 8, 9, 17, 45, 0, 408, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 8, 9, 17, 45, 0, 408, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 8, 9, 17, 45, 0, 408, DateTimeKind.Utc),
    };

    private static VoiceNoteDocument Populated()
    {
        var note = Fresh();
        note.Attempts = 3;
        note.MaxAttempts = 4;
        note.LockedUntil = DateTime.UtcNow;
        note.LastError = "boom";
        note.NotifiedAt = DateTime.UtcNow;
        note.Transcript = "remember the milk";
        return note;
    }

    private static JsonElement Serialize(VoiceNoteDocument note) =>
        JsonSerializer.SerializeToElement(
            note.ToDto(),
            Life_Admin_Autopilot_Backend.Kernel.Json.KernelJson.Lenient);
}
