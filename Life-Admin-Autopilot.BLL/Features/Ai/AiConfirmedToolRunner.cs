using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// Port of <c>runConfirmedTool</c> — the dispatch that runs AFTER the user pressed
/// Confirm.
///
/// <para>
/// <b>Exactly one tool can arrive here.</b> <c>deleteAllTasks</c> is the only name
/// <see cref="AiToolCatalog.RequiresConfirmation"/> gates, so it is the only name
/// that can ever be staged as a pending record and therefore the only one this
/// runner implements. Every other tool runs inline inside the agent and never
/// reaches a confirmation card; asked to run one, this refuses with Node's
/// <c>tool_not_destructive</c> rather than quietly doing it.
/// </para>
///
/// <para>
/// <b>The delete is soft and journaled.</b> It goes through
/// <see cref="BulkService"/>, which writes one reversible <c>TaskBulkOp</c> before
/// touching a task. The confirmation card is the speed bump; the undo token is the
/// seatbelt, and it exists even for "clear everything".
/// </para>
/// </summary>
public sealed class AiConfirmedToolRunner
{
    /// <summary>The bulk-op label the chat path stamps, so an undo toast can name its source.</summary>
    public const string BulkLabel = "Cleared from chat";

    private readonly BulkService _bulk;
    private readonly IMongoCollection<ClarificationDocument> _clarifications;

    public AiConfirmedToolRunner(BulkService bulk, IMongoDatabase database)
    {
        _bulk = bulk;
        _clarifications = database.GetCollection<ClarificationDocument>(MongoCollections.Clarifications);
    }

    /// <summary>
    /// Run a confirmed tool. Throws <see cref="AppException"/> on refusal or failure;
    /// the caller turns that into the <c>error</c> member of the <c>tool_result</c>
    /// frame, because by then the headers have flushed.
    /// </summary>
    public Task<IReadOnlyDictionary<string, object?>> RunAsync(
        ObjectId userId,
        string name,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        if (!AiToolCatalog.RequiresConfirmation(name))
        {
            throw AppException.BadRequest(
                "tool_not_destructive",
                $"{name} runs inline — no confirmation step");
        }

        return RunDeleteAllAsync(userId, args, cancellationToken);
    }

    /// <summary>
    /// <c>runDeleteAll</c>. The result object below is the <c>result</c> member of the
    /// <c>tool_result</c> frame and is rendered by the client, so its six keys and
    /// their order are copied from the Node return.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, object?>> RunDeleteAllAsync(
        ObjectId userId,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        var domain = args.TryGetValue("domain", out var d) ? d as string : null;
        var status = args.TryGetValue("status", out var s) ? s as string : null;

        var filter = new TaskQuery.TaskFilter
        {
            Domain = domain is null ? null : new[] { domain },
            Status = status is null ? null : new[] { status },
        };

        var result = await _bulk
            .ApplyAsync(
                userId,
                new BulkTarget { Filter = filter },
                new BulkActionInput.Delete(),
                BulkLabel,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Held items are not tasks yet, so the wipe above cannot reach them — drop
        // them explicitly or "clear everything" leaves questions standing on the
        // dashboard. Skipped for a status-scoped wipe: a held item has no task
        // status to match.
        var dropped = status is null
            ? await DropOpenClarificationsAsync(userId, domain, cancellationToken).ConfigureAwait(false)
            : 0L;

        return new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["deleted"] = true,
            ["deletedCount"] = result.Affected,
            ["droppedQuestionCount"] = dropped,
            ["undoToken"] = result.UndoToken,
            ["domain"] = domain,
            ["status"] = status,
        };
    }

    /// <summary>
    /// <c>dropOpenClarifications</c> — every open question for this user, optionally
    /// narrowed to one domain, marked <c>dropped</c> with a resolution timestamp.
    /// </summary>
    private async Task<long> DropOpenClarificationsAsync(
        ObjectId userId,
        string? domain,
        CancellationToken cancellationToken)
    {
        var conditions = new List<FilterDefinition<ClarificationDocument>>(3)
        {
            Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
            Builders<ClarificationDocument>.Filter.Eq(c => c.Status, ClarificationVocabulary.Open),
        };

        if (domain is not null)
        {
            conditions.Add(Builders<ClarificationDocument>.Filter.Eq(c => c.Draft.Domain, domain));
        }

        var now = DateTime.UtcNow;

        var update = Builders<ClarificationDocument>.Update
            .Set(c => c.Status, "dropped")
            .Set(c => c.ResolvedAt, now)

            // Mongoose's `timestamps: true` injects this into the $set; the .NET
            // driver does not, and a stale updatedAt is observable through
            // GET /me/export.
            .Set(c => c.UpdatedAt, now);

        var result = await _clarifications
            .UpdateManyAsync(
                Builders<ClarificationDocument>.Filter.And(conditions),
                update,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount;
    }
}
