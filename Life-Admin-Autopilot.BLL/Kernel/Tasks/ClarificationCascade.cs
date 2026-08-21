using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Kernel.Tasks;

/// <summary>
/// Port of <c>server/src/modules/clarifications/dropOpenClarifications.ts</c>.
///
/// <para>
/// Lives in the kernel rather than the clarifications slice because
/// <see cref="BulkService"/> — the only journaled delete path — depends on it.
/// A question is ABOUT a task; once the task is gone there is nothing left to
/// clarify, and leaving it open strands a prompt the user can never meaningfully
/// answer. That is exactly what made "Questions for you" outlive everything the
/// user had cleared.
/// </para>
/// </summary>
public sealed class ClarificationCascade
{
    private readonly IMongoCollection<ClarificationDocument> _clarifications;

    /// <summary>Kept for the notifications collection — see SettleDateQuestionsAsync.</summary>
    private readonly IMongoDatabase _database;

    public ClarificationCascade(IMongoDatabase database)
    {
        _database = database;
        _clarifications = database.GetCollection<ClarificationDocument>(MongoCollections.Clarifications);
    }

    /// <summary>Per-task cascade — the delete path for one or many specific tasks.</summary>
    public async Task<long> DropForTasksAsync(
        ObjectId userId,
        IReadOnlyList<ObjectId> taskIds,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        var result = await _clarifications
            .UpdateManyAsync(
                Builders<ClarificationDocument>.Filter.And(
                    Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
                    Builders<ClarificationDocument>.Filter.Eq(c => c.Status, ClarificationVocabulary.Open),
                    // TaskId is ObjectId? because legacy rows have no taskId at all;
                    // the ids being cascaded are always concrete, so lift them.
                    Builders<ClarificationDocument>.Filter.In(
                        c => c.TaskId,
                        taskIds.Select(id => (ObjectId?)id))),
                Builders<ClarificationDocument>.Update
                    .Set(c => c.Status, ClarificationVocabulary.Dropped)
                    .Set(c => c.ResolvedAt, DateTime.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }

    /// <summary>
    /// The user answered by doing, rather than by answering.
    ///
    /// <para>
    /// A matter filed with no date carries an open question asking for one, and the
    /// pop-up that raises it sends the user to the matter itself — because picking a
    /// date there gets the app's real editor, and a clash shows the way a clash shows
    /// everywhere else. But setting the date that way used to leave the question
    /// standing: it is only closed by the resolve endpoint, and nothing on the task
    /// write path told it anything had happened. The only safety net was
    /// <c>StaleClarificationSettler</c>, which drops an unanswered question after
    /// SEVEN DAYS — so the app went on asking for a date it had already been given,
    /// for a week.
    /// </para>
    ///
    /// <para>
    /// Scoped to <c>date</c> questions on purpose. A duplicate question ("file this
    /// as well?") and a confirmation ("did you mean to file this?") are not answered
    /// by a date arriving, and closing them here would silently discard a question
    /// the user has not addressed.
    /// </para>
    ///
    /// <para>
    /// Recorded as <b>resolved</b> rather than dropped, and with the answer naming
    /// what actually happened: dropping says "the user declined to answer", which is
    /// the opposite of what they did.
    /// </para>
    /// </summary>
    /// <returns>How many questions this closed. Zero is the ordinary case.</returns>
    public async Task<long> SettleDateQuestionsAsync(
        ObjectId userId,
        ObjectId taskId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ClarificationDocument>.Filter.And(
            Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<ClarificationDocument>.Filter.Eq(c => c.TaskId, taskId),
            Builders<ClarificationDocument>.Filter.Eq(c => c.Status, ClarificationVocabulary.Open),
            Builders<ClarificationDocument>.Filter.Eq(c => c.Kind, "date"));

        // Read the ids BEFORE the update, because the bell rows that pointed at
        // these questions have to go too and UpdateMany does not report what it
        // touched. Two round trips on a path that finds nothing the vast majority
        // of the time; the alternative is a dead link in the bell.
        var ids = await _clarifications
            .Find(filter)
            .Project(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ids.Count == 0)
        {
            return 0;
        }

        var result = await _clarifications
            .UpdateManyAsync(
                Builders<ClarificationDocument>.Filter.In(c => c.Id, ids),
                Builders<ClarificationDocument>.Update
                    .Set(c => c.Status, ClarificationVocabulary.Resolved)
                    .Set(c => c.Answer, AnsweredByEditing)
                    .Set(c => c.ResolvedAt, now)
                    // Mongoose stamps this itself on a timestamps:true model; the
                    // .NET driver does not. See KERNEL.md 7.0.
                    .Set(c => c.UpdatedAt, now),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await _database
            .GetCollection<NotificationDocument>(MongoCollections.Notifications)
            .DeleteManyAsync(
                Builders<NotificationDocument>.Filter.In(
                    n => n.ClarificationId,
                    ids.Select(id => (ObjectId?)id)),
                cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }

    /// <summary>The answer written onto a question the user settled by editing the matter.</summary>
    public const string AnsweredByEditing = "Answered by setting the date.";

    /// <summary>
    /// Blanket drop for a "clear everything" wipe, optionally scoped by
    /// <c>draft.domain</c>.
    ///
    /// <para>A STATUS-scoped wipe deliberately drops nothing: status describes a
    /// task's lifecycle, and the per-task cascade above already covers whatever
    /// that wipe actually deleted. Pass no domain only for a true blanket wipe.</para>
    /// </summary>
    public async Task<long> DropOpenAsync(
        ObjectId userId,
        string? domain = null,
        CancellationToken cancellationToken = default)
    {
        var clauses = new List<FilterDefinition<ClarificationDocument>>
        {
            Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<ClarificationDocument>.Filter.Eq(c => c.Status, ClarificationVocabulary.Open),
        };

        if (!string.IsNullOrEmpty(domain))
        {
            clauses.Add(Builders<ClarificationDocument>.Filter.Eq("draft.domain", domain));
        }

        var result = await _clarifications
            .UpdateManyAsync(
                Builders<ClarificationDocument>.Filter.And(clauses),
                Builders<ClarificationDocument>.Update
                    .Set(c => c.Status, ClarificationVocabulary.Dropped)
                    .Set(c => c.ResolvedAt, DateTime.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }
}
