using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.DocumentScans;

/// <summary>
/// The <c>toJSON</c> transform. These tests are about what is NOT in the payload
/// as much as what is — nine fields are stripped, and a leak of any one of them
/// is a real disclosure (<c>storageKey</c> points at the user's document,
/// <c>rawExtractedText</c> IS the document).
/// </summary>
public sealed class DocumentScanMapperTests
{
    /// <summary>Every field the Mongoose transform deletes.</summary>
    private static readonly string[] StrippedFields =
    {
        "storageKey", "rawExtractedText", "attempts", "maxAttempts",
        "manualRetries", "lockedUntil", "nextRunAt", "lastError", "notifiedAt",
    };

    [Fact]
    public void strips_all_nine_internal_fields()
    {
        // Arrange — every stripped field populated, so a leak cannot hide behind a null.
        var document = Populated();

        // Act
        var json = Serialize(document);

        // Assert
        foreach (var field in StrippedFields)
        {
            Assert.False(
                json.TryGetProperty(field, out _),
                $"'{field}' must never reach a client.");
        }
    }

    [Fact]
    public void keeps_failureReason_even_though_lastError_is_stripped()
    {
        // Arrange — the two usually hold the SAME string; only one is product state.
        var document = Populated();
        document.FailureReason = "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.";
        document.LastError = document.FailureReason;

        // Act
        var json = Serialize(document);

        // Assert
        Assert.Equal(document.FailureReason, json.GetProperty("failureReason").GetString());
        Assert.False(json.TryGetProperty("lastError", out _));
    }

    [Theory]
    [InlineData("failed", 0, true)]
    [InlineData("failed", 2, true)]
    [InlineData("failed", 3, false)]
    [InlineData("failed", 4, false)]
    [InlineData("pending", 0, false)]
    [InlineData("processing", 0, false)]
    [InlineData("ready_for_review", 0, false)]
    public void derives_canRetry_from_status_and_the_hidden_retry_counter(
        string status,
        int manualRetries,
        bool expected)
    {
        // Arrange
        var document = Populated();
        document.Status = status;
        document.ManualRetries = manualRetries;

        // Act
        var dto = document.ToDto();

        // Assert
        Assert.Equal(expected, dto.CanRetry);
    }

    [Fact]
    public void omits_unset_optional_fields_rather_than_emitting_null()
    {
        // Arrange — Mongoose never stores an unset optional, so it never appears.
        var document = Minimal();

        // Act
        var json = Serialize(document);

        // Assert
        foreach (var field in new[]
                 {
                     "timezone", "failureReason", "documentSummary", "documentType",
                     "documentTitle", "documentSubtitle", "issuer", "reviewedAt",
                 })
        {
            Assert.False(json.TryGetProperty(field, out _), $"'{field}' should be absent, not null.");
        }
    }

    [Fact]
    public void emits_keys_in_the_order_the_reference_server_emits_them()
    {
        // Arrange — Mongoose serialises in first-set order, so the worker-written
        // fields land AFTER updatedAt. Captured from the live reference.
        var document = Minimal();
        document.Timezone = "Africa/Cairo";
        document.Status = "failed";
        document.FailureReason = "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.";

        // Act
        var keys = Serialize(document).EnumerateObject().Select(p => p.Name).ToArray();

        // Assert
        Assert.Equal(
            new[]
            {
                "userId", "mimeType", "sourceType", "pageCount", "byteSize", "status",
                "clientCapturedAt", "timezone", "candidates", "createdAt", "updatedAt",
                "failureReason", "id", "canRetry",
            },
            keys);
    }

    [Fact]
    public void exposes_a_candidate_taskId_as_a_hex_string_and_omits_it_when_unaccepted()
    {
        // Arrange
        var accepted = ObjectId.GenerateNewId();
        var document = Minimal();
        document.Candidates =
        [
            new ExtractedTaskCandidateDocument { Key = "a", Title = "Pay it", Domain = "finance", TaskId = accepted },
            new ExtractedTaskCandidateDocument { Key = "b", Title = "File it", Domain = "home" },
        ];

        // Act
        var candidates = Serialize(document).GetProperty("candidates").EnumerateArray().ToArray();

        // Assert
        Assert.Equal(accepted.ToString(), candidates[0].GetProperty("taskId").GetString());
        Assert.False(candidates[1].TryGetProperty("taskId", out _));
    }

    private static ScannedDocumentDocument Minimal() => new()
    {
        Id = ObjectId.GenerateNewId(),
        UserId = ObjectId.GenerateNewId(),
        StorageKey = "user/scan.pdf",
        MimeType = "application/pdf",
        SourceType = "pdf",
        PageCount = 1,
        ByteSize = 193,
        Status = "pending",
        ClientCapturedAt = new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc),
        NextRunAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ScannedDocumentDocument Populated()
    {
        var document = Minimal();
        document.RawExtractedText = "the whole document, in plain text";
        document.Attempts = 3;
        document.MaxAttempts = 4;
        document.ManualRetries = 1;
        document.LockedUntil = DateTime.UtcNow;
        document.LastError = "boom";
        document.NotifiedAt = DateTime.UtcNow;
        return document;
    }

    private static JsonElement Serialize(ScannedDocumentDocument document) =>
        JsonSerializer.SerializeToElement(
            document.ToDto(),
            Life_Admin_Autopilot_Backend.Kernel.Json.KernelJson.Lenient);
}
