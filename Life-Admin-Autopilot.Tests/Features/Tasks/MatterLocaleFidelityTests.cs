using System.Reflection;
using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Tasks;

/// <summary>
/// The locale overlay must change the WORDS and nothing else.
///
/// <para>
/// <c>GET /me/tasks</c> and <c>GET /me/tasks/{id}</c> are the only two routes that
/// run a matter through <c>MatterLocale</c>, and the overlay used to rebuild the
/// document from a hand-written list of its properties. The list fell four behind
/// — <c>Amount</c>, <c>SchemaVersion</c>, <c>GoogleEventId</c>,
/// <c>GooglePushedAt</c> — and the one that reached the wire took a real feature
/// down with it: a matter with a price answered <c>amount: null</c> on exactly the
/// two endpoints the app reads to display and edit that price, while the finance
/// summary reported it correctly from the same documents.
/// </para>
///
/// <para>
/// The bug was invisible in every other way. Nothing threw, no field was wrong,
/// one was simply missing. So the test is written by REFLECTION over the document
/// rather than as a list of assertions — a list is the same artefact that failed,
/// and it would fall behind for the same reason.
/// </para>
/// </summary>
public sealed class MatterLocaleFidelityTests
{
    /// <summary>
    /// Set every property to a non-default value, so "was it copied?" can be
    /// answered by comparing against the default rather than by naming each field.
    /// </summary>
    private static TaskDocument FullyPopulated() => new()
    {
        Id = ObjectId.GenerateNewId(),
        SchemaVersion = 7,
        UserId = ObjectId.GenerateNewId(),
        Title = "Pay the internet bill",
        Domain = "finance",
        Kind = "reminder",
        Status = "open",
        Priority = "high",
        Subtasks = [new SubtaskDocument { Id = ObjectId.GenerateNewId(), Text = "check the meter", Done = false }],
        Tags = ["bills"],
        DueAt = new DateTime(2026, 8, 24, 6, 0, 0, DateTimeKind.Utc),
        Notes = "quarterly",
        SourceLocale = "ar",
        SourceVoiceNoteId = ObjectId.GenerateNewId(),
        SourceDocumentId = ObjectId.GenerateNewId(),
        SourceTaskKey = "key-1",
        ExternalSource = "google",
        ExternalId = "ext-1",
        GoogleEventId = "gcal-1",
        GooglePushedAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
        TimePrecision = "time",
        Confidence = "high",
        Estimate = new TaskEstimateDocument { MinMinutes = 20, MaxMinutes = 40, Source = "ai" },

        // The field whose loss started this test.
        Amount = MoneyVocabulary.FromMinor(73_000, "EGP", "user", "out"),
        CompletedAt = new DateTime(2026, 8, 25, 6, 0, 0, DateTimeKind.Utc),
        SnoozedUntil = new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc),
        Reminders = [new ReminderEntryDocument { At = new DateTime(2026, 8, 24, 5, 0, 0, DateTimeKind.Utc) }],
        DeletedAt = null,
        RescheduleCount = 2,
        CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Every property on the document survives the copy, except <c>I18n</c>, which
    /// the overlay clears on purpose.
    /// </summary>
    [Fact]
    public void the_overlay_carries_every_field_the_document_has()
    {
        var source = FullyPopulated();
        var copy = source.ShallowCopy();

        var dropped = typeof(TaskDocument)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => !Equals(p.GetValue(source), p.GetValue(copy)))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(dropped);
    }

    /// <summary>
    /// The regression itself, at the level the app sees it: a priced matter read
    /// through the overlay still has its price.
    /// </summary>
    [Fact]
    public void a_priced_matter_keeps_its_amount_through_the_overlay()
    {
        var dto = MatterLocale.Present(FullyPopulated(), "en");

        Assert.NotNull(dto.Amount);
        Assert.Equal(73_000, dto.Amount!.AmountMinor);
        Assert.Equal("EGP", dto.Amount.Currency);
        Assert.Equal("out", dto.Amount.Direction);
    }

    /// <summary>
    /// And it survives the branch that actually rewrites text, which is a different
    /// code path from the no-translation early return.
    /// </summary>
    [Fact]
    public void the_amount_survives_a_matter_that_is_actually_translated()
    {
        var doc = FullyPopulated();
        doc.I18n = new Dictionary<string, TaskTranslationDocument>
        {
            ["en"] = new() { Title = "Pay the internet bill", Notes = "quarterly" },
        };

        var dto = MatterLocale.Present(doc, "en");

        Assert.Equal("Pay the internet bill", dto.Title);
        Assert.NotNull(dto.Amount);
        Assert.Equal(73_000, dto.Amount!.AmountMinor);
    }

    /// <summary>
    /// <c>i18n</c> is still stripped — the client renders one language and the
    /// payload must not carry every translation of every row.
    /// </summary>
    [Fact]
    public void the_translation_table_is_still_stripped()
    {
        var doc = FullyPopulated();
        doc.I18n = new Dictionary<string, TaskTranslationDocument>
        {
            ["en"] = new() { Title = "Pay the internet bill" },
        };

        // The overlay must not have cleared it on the caller's own document either.
        _ = MatterLocale.Present(doc, "en");
        Assert.NotNull(doc.I18n);
    }

    /// <summary>
    /// The copy must not let a presenter mutate the caller's document. The subtask
    /// overlay assigns a new list rather than editing in place, and this pins that
    /// — a shallow copy shares the list reference, so an in-place edit would reach
    /// the document the repository still holds.
    /// </summary>
    [Fact]
    public void translating_subtasks_does_not_touch_the_original_document()
    {
        var doc = FullyPopulated();
        var originalText = doc.Subtasks[0].Text;
        doc.I18n = new Dictionary<string, TaskTranslationDocument>
        {
            ["en"] = new()
            {
                Title = "Pay the internet bill",

                // "undefined" is the frozen lookup key the Node port reproduces.
                Subtasks = new Dictionary<string, string> { ["undefined"] = "read the meter" },
            },
        };

        var dto = MatterLocale.Present(doc, "en");

        Assert.Equal("read the meter", dto.Subtasks[0].Text);
        Assert.Equal(originalText, doc.Subtasks[0].Text);
    }
}
