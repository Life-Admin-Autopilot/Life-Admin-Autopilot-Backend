using System.Text.Json;
using Life_Admin_Autopilot.BLL.Kernel.Json;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The <c>toJSON</c> transforms. These guard the leaks and the derived fields —
/// the two things a direct entity serialization gets wrong.
/// </summary>
public sealed class KernelMappersTests
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    [Fact]
    public void user_drops_the_password_hash_and_derives_has_password()
    {
        // Arrange
        var doc = new UserProfileDocument
        {
            Id = ObjectId.Parse("6a78c216aa461ae1dc64ab59"),
            Email = "kernel@probe.com",
            PasswordHash = "$argon2id$v=19$m=65536",
        };

        // Act
        var json = JsonSerializer.Serialize(doc.ToDto(), Options);

        // Assert
        Assert.DoesNotContain("passwordHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", json, StringComparison.Ordinal);
        Assert.Contains("\"hasPassword\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void user_reports_has_password_false_for_a_magic_link_account()
    {
        // Assert — a magic-link account has no password, so demanding one before a
        // destructive action would ask for something the user cannot give.
        Assert.False(new UserProfileDocument { Email = "x@y.com" }.ToDto().HasPassword);
    }

    [Fact]
    public void user_omits_absent_optional_fields_rather_than_emitting_null()
    {
        // Act
        var json = JsonSerializer.Serialize(new UserProfileDocument { Email = "x@y.com" }.ToDto(), Options);

        // Assert — Mongoose never stores an unset optional, so it never appears.
        Assert.DoesNotContain("displayName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingEmail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("emailVerifiedAt", json, StringComparison.Ordinal);
    }

    [Fact]
    public void task_maps_id_and_derives_priority_rank()
    {
        // Arrange
        var doc = new TaskDocument
        {
            Id = ObjectId.Parse("6a78c216aa461ae1dc64ab5a"),
            UserId = ObjectId.Parse("6a78c216aa461ae1dc64ab59"),
            Title = "Renew passport",
            Domain = "home",
            Priority = "urgent",
        };

        // Act
        var dto = doc.ToDto();

        // Assert
        Assert.Equal("6a78c216aa461ae1dc64ab5a", dto.Id);
        Assert.Equal(3, dto.PriorityRank);
    }

    [Fact]
    public void task_falls_back_to_the_normal_rank_for_an_unknown_priority()
    {
        // Assert — matches Node's `?? PRIORITY_RANK.normal`.
        Assert.Equal(1, new TaskDocument { Priority = "bogus" }.ToDto().PriorityRank);
    }

    [Fact]
    public void subtasks_get_their_own_id_mapping()
    {
        // Arrange — Mongoose does not recurse a parent transform into subdocuments,
        // so without a dedicated transform subtasks ship _id and no id, breaking
        // React keys and subtask mutate-by-id.
        var subId = ObjectId.GenerateNewId();
        var doc = new TaskDocument
        {
            Subtasks = new List<SubtaskDocument> { new() { Id = subId, Text = "step" } },
        };

        // Act
        var json = JsonSerializer.Serialize(doc.ToDto(), Options);

        // Assert
        Assert.Contains($"\"id\":\"{subId}\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("_id", json, StringComparison.Ordinal);
    }

    [Fact]
    public void clarification_drops_the_internal_source_key()
    {
        // Arrange
        var doc = new ClarificationDocument
        {
            Id = ObjectId.GenerateNewId(),
            SourceKey = "voice:note-1:item-2",
            Question = "Which date?",
        };

        // Act
        var json = JsonSerializer.Serialize(doc.ToDto(), Options);

        // Assert
        Assert.DoesNotContain("sourceKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("voice:note-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void timestamps_serialize_like_javascript_to_iso_string()
    {
        // Arrange — a whole-second instant. STJ's default round-trip format would
        // drop the fraction entirely; Node always writes three digits.
        var doc = new NotificationDocument
        {
            Id = ObjectId.GenerateNewId(),
            Kind = "reminder",
            Title = "Due today",
            CreatedAt = new DateTime(2026, 8, 9, 18, 8, 22, DateTimeKind.Utc),
        };

        // Act
        var json = JsonSerializer.Serialize(doc.ToDto(), Options);

        // Assert
        Assert.Contains("\"createdAt\":\"2026-08-09T18:08:22.000Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void fractional_timestamps_keep_exactly_three_digits()
    {
        // Assert — .600 must not be trimmed to .6.
        Assert.Equal(
            "2026-08-09T18:08:22.600Z",
            JsIsoDateTimeConverter.ToIso(new DateTime(2026, 8, 9, 18, 8, 22, 600, DateTimeKind.Utc)));
    }

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsIsoDateTimeConverter());
        options.Converters.Add(new JsIsoNullableDateTimeConverter());
        return options;
    }
}
