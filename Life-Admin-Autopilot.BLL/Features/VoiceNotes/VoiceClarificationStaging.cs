using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// Turns the note's staged clarify lane into real, answerable Clarifications.
/// Port of <c>persistClarifications</c> in <c>lib/voiceNoteTranscriber.ts</c>
/// plus <c>modules/clarifications/upsertVoiceClarification.ts</c>.
///
/// <para>
/// <b>The task comes FIRST and always.</b> A held item used to be a draft with no
/// Task behind it, so a captured thought sat invisible until answered — absent
/// from Matters, unsearchable, and untouched by "delete everything". The first
/// option is the model's most-likely guess (it orders them), so it becomes the
/// provisional date.
/// </para>
///
/// <para>
/// Sequential, not parallel: the lists are tiny and this keeps Mongo write
/// pressure flat, matching Node's deliberate <c>for…of</c> over a
/// <c>Promise.all</c>.
/// </para>
/// </summary>
public interface IVoiceClarificationStaging
{
    Task PersistAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVoiceClarificationStaging"/>
public sealed class VoiceClarificationStaging : IVoiceClarificationStaging
{
    /// <summary>
    /// Longest quote kept on a Clarification — roughly a short paragraph, well past
    /// what the card shows. <c>MAX_SOURCE_TEXT</c> in
    /// <c>modules/clarifications/sourceQuote.ts</c>.
    /// </summary>
    public const int MaxSourceText = 600;

    private readonly IVoiceNoteTaskPersistence _tasks;
    private readonly IMongoCollection<BsonDocument> _clarifications;

    public VoiceClarificationStaging(IVoiceNoteTaskPersistence tasks, IMongoDatabase database)
    {
        _tasks = tasks;
        _clarifications = database.GetCollection<BsonDocument>(MongoCollections.Clarifications);
    }

    public async Task PersistAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default)
    {
        foreach (var item in note.ClarifyItems)
        {
            var guess = item.Options.Count > 0 ? item.Options[0].DueAt : null;

            var created = await _tasks
                .PersistAsync(
                    note.UserId,
                    note.Id,
                    new[]
                    {
                        new VoiceTaskSeed(
                            item.Key,
                            item.Title,
                            item.Domain,
                            item.Priority,

                            // Forced, not carried: a question is by definition not a
                            // confident reading, and 'vague_date' is what the card
                            // renders as the reason.
                            Confidence: "low",
                            Estimate: null,
                            DueAt: guess,
                            Notes: item.Notes),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (created.Count == 0)
            {
                continue;
            }

            await UpsertAsync(note, item, created[0].Id, guess, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Idempotent via the note-scoped item key: the partial unique index on
    /// <c>{userId, sourceKey}</c> plus <c>$setOnInsert</c> means a reclaim or retry
    /// never duplicates a question, and never REOPENS one the user already answered
    /// — the row is matched but not rewritten.
    /// </summary>
    private Task UpsertAsync(
        VoiceNoteDocument note,
        VoiceClarifyItemDocument item,
        ObjectId taskId,
        DateTime? guess,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var draft = new BsonDocument
        {
            ["title"] = item.Title,
            ["domain"] = item.Domain,
            ["priority"] = item.Priority,
            ["tags"] = new BsonArray(),
        };

        if (!string.IsNullOrEmpty(item.Notes))
        {
            draft["notes"] = item.Notes;
        }

        if (guess is not null)
        {
            draft["dueAt"] = guess.Value;
        }

        var seed = new BsonDocument
        {
            ["userId"] = note.UserId,
            ["taskId"] = taskId,
            ["sourceKey"] = item.Key,
            ["status"] = "open",
            ["draft"] = draft,
            ["question"] = item.Question,
            ["kind"] = item.Kind,
            ["costOfWrong"] = item.CostOfWrong,
            ["options"] = new BsonArray(item.Options.Select(OptionOf)),
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        // The WHOLE transcript, not a per-item slice: extraction returns titles and
        // questions, never the span of speech each one came from, and a guessed
        // slice would quote the user saying something they did not.
        var sourceText = ClampSourceText(note.Transcript);
        if (sourceText is not null)
        {
            seed["sourceText"] = sourceText;
        }

        return _clarifications.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", note.UserId),
                Builders<BsonDocument>.Filter.Eq("sourceKey", item.Key)),
            new BsonDocument("$setOnInsert", seed),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    private static BsonDocument OptionOf(VoiceClarifyOptionDocument option)
    {
        var document = new BsonDocument { ["label"] = option.Label };
        if (option.DueAt is not null)
        {
            document["dueAt"] = option.DueAt.Value;
        }

        return document;
    }

    /// <summary>
    /// Trim, cap, and drop the empty case. Returns <c>null</c> rather than an empty
    /// string so the field is simply ABSENT on rows with nothing worth quoting.
    /// </summary>
    public static string? ClampSourceText(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxSourceText
            ? trimmed
            : $"{trimmed[..(MaxSourceText - 1)].TrimEnd()}…";
    }
}
