using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
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
    /// <returns>
    /// How many of the note's items are HELD — that is, now have a Clarification row,
    /// whether this pass inserted it or found it already there. Not the same as
    /// <c>ClarifyItems.Count</c>: past the open-queue cap an item is filed with the
    /// guess and no question is raised at all. This is the number the completion
    /// notification reports, so it has to survive a worker reclaim unchanged.
    /// </returns>
    Task<int> PersistAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IVoiceClarificationStaging"/>
public sealed class VoiceClarificationStaging : IVoiceClarificationStaging
{
    /// <summary>
    /// Longest quote kept on a Clarification — roughly a short paragraph, well past
    /// what the card shows. <c>MAX_SOURCE_TEXT</c> in
    /// <c>modules/clarifications/sourceQuote.ts</c>, which is where the port keeps it
    /// too: this is an alias for <see cref="SourceQuote.MaxSourceText"/>, not a second
    /// ceiling.
    /// </summary>
    public const int MaxSourceText = SourceQuote.MaxSourceText;

    private readonly IVoiceNoteTaskPersistence _tasks;
    private readonly ClarificationRepository _repository;
    private readonly VoiceNoteOutcomeNotifier _notifier;
    private readonly IMongoCollection<BsonDocument> _clarifications;

    public VoiceClarificationStaging(
        IVoiceNoteTaskPersistence tasks,
        ClarificationRepository repository,
        VoiceNoteOutcomeNotifier notifier,
        IMongoDatabase database)
    {
        _tasks = tasks;
        _repository = repository;
        _notifier = notifier;
        _clarifications = database.GetCollection<BsonDocument>(MongoCollections.Clarifications);
    }

    public async Task<int> PersistAsync(VoiceNoteDocument note, CancellationToken cancellationToken = default)
    {
        var held = 0;

        foreach (var item in note.ClarifyItems)
        {
            // Option zero is the reading the item was filed under, for every date
            // question. A question with no options — "how much is this?" — falls
            // back to the item's own date, or the matter loses a day the user gave.
            var guess = item.Options.Count > 0 ? item.Options[0].DueAt : item.DueAt;

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

            // The TASK is unconditional and already written by the line above. Only
            // the QUESTION is subject to the cap — past it the item is filed with the
            // guess and nothing is asked, because a slightly-wrong but visible matter
            // beats a question the user never reaches. Same rule and same constant as
            // the chat lane's ClarificationHoldService; checked per item rather than
            // per note, since each question this pass raises fills a slot.
            if (await IsAtCapAsync(note.UserId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var clarificationId = await UpsertAsync(note, item, created[0].Id, guess, cancellationToken)
                .ConfigureAwait(false);

            // Held either way: the question exists after this line whether we wrote it
            // or found it.
            held++;

            // Null when the upsert MATCHED rather than inserted — a worker reclaim
            // re-running the same idempotent write. Notifying there would turn one
            // wedged note into a stream of duplicate rows for a question the user has
            // already been shown.
            if (clarificationId is not { } id)
            {
                continue;
            }

            await _notifier
                .UncertaintyAsync(note.UserId, created[0].Id, id, item.Question, item.Title, cancellationToken)
                .ConfigureAwait(false);
        }

        return held;
    }

    /// <summary>
    /// <c>MAX_OPEN_CLARIFICATIONS</c>, shared with the chat lane rather than
    /// re-declared. Deliberately a bare open count and not <c>VisibleOpen()</c>: a
    /// question the user skipped is still queued and still comes back, so it still
    /// occupies a slot.
    ///
    /// <para>
    /// This lane had no cap at all until the auto-file policy landed, which was
    /// survivable while only genuinely vague items were held. It is not survivable
    /// now: every assumed clock time raises a question, so one long dictation could
    /// fill the queue on its own and bury whatever was already in it.
    /// </para>
    /// </summary>
    private async Task<bool> IsAtCapAsync(ObjectId userId, CancellationToken cancellationToken) =>
        await _repository.CountOpenAsync(userId, cancellationToken).ConfigureAwait(false)
        >= ClarificationHoldService.MaxOpenClarifications;

    /// <summary>
    /// Idempotent via the note-scoped item key: the partial unique index on
    /// <c>{userId, sourceKey}</c> plus <c>$setOnInsert</c> means a reclaim or retry
    /// never duplicates a question, and never REOPENS one the user already answered
    /// — the row is matched but not rewritten.
    /// </summary>
    /// <returns>
    /// The new clarification's id, or null when the row already existed. That
    /// distinction is the only honest signal that this pass RAISED a question rather
    /// than re-running a write, and it is what keeps the notification feed from
    /// repeating itself after a reclaim.
    /// </returns>
    private async Task<ObjectId?> UpsertAsync(
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
            ["status"] = ClarificationVocabulary.Open,
            ["draft"] = draft,
            ["question"] = item.Question,
            ["kind"] = item.Kind,
            ["costOfWrong"] = item.CostOfWrong,
            ["options"] = new BsonArray(item.Options.Select(OptionOf)),
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        // Rendered in place of `question` by a client holding the catalogue.
        // `question` itself stays as the English fallback, for anything that is not
        // one: an older build, and the admin console's read-only views.
        if (item.QuestionKey is { Length: > 0 })
        {
            seed["questionKey"] = item.QuestionKey;
            seed["questionParams"] = ParamsOf(item.QuestionParams);
        }

        // The WHOLE transcript, not a per-item slice: extraction returns titles and
        // questions, never the span of speech each one came from, and a guessed
        // slice would quote the user saying something they did not.
        var sourceText = ClampSourceText(note.Transcript);
        if (sourceText is not null)
        {
            seed["sourceText"] = sourceText;
        }

        var result = await _clarifications.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", note.UserId),
                Builders<BsonDocument>.Filter.Eq("sourceKey", item.Key)),
            new BsonDocument("$setOnInsert", seed),
            new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);

        // Present only on an INSERT. `IsAcknowledged` is checked because an
        // unacknowledged write cannot report one either way, and guessing "inserted"
        // there would notify for a row that may not exist.
        return result.IsAcknowledged && result.UpsertedId is { } id ? id.AsObjectId : null;
    }

    private static BsonDocument OptionOf(VoiceClarifyOptionDocument option)
    {
        var document = new BsonDocument { ["label"] = option.Label };
        if (option.DueAt is not null)
        {
            document["dueAt"] = option.DueAt.Value;
        }

        if (option.LabelKey is { Length: > 0 })
        {
            document["labelKey"] = option.LabelKey;
            document["labelParams"] = ParamsOf(option.LabelParams);
        }

        return document;
    }

    /// <summary>
    /// i18n values as a BSON sub-document, always present beside a key even when
    /// empty. A key with no params (<c>chip.keepBoth</c>) and a key whose params
    /// failed to stage look identical otherwise, and the client would have to guess
    /// which it is holding.
    /// </summary>
    private static BsonDocument ParamsOf(IReadOnlyDictionary<string, string>? values)
    {
        var document = new BsonDocument();

        foreach (var (key, value) in values ?? new Dictionary<string, string>())
        {
            document[key] = value;
        }

        return document;
    }

    /// <summary>
    /// Trim, cap, and drop the empty case. Returns <c>null</c> rather than an empty
    /// string so the field is simply ABSENT on rows with nothing worth quoting.
    ///
    /// <para>
    /// Forwards to <see cref="SourceQuote.Clamp"/>. It used to be a second
    /// implementation, written before the clarifications slice had one; the two
    /// writers of <c>sourceText</c> must clamp identically or the same transcript
    /// gets two different quotes depending on which lane stored it.
    /// </para>
    /// </summary>
    public static string? ClampSourceText(string? text) => SourceQuote.Clamp(text);
}
