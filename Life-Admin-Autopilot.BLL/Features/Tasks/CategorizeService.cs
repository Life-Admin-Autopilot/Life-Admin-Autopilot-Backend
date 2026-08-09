using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

public sealed class ProposedChangeDto
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("fromDomain")]
    public string FromDomain { get; init; } = string.Empty;

    [JsonPropertyName("toDomain")]
    public string ToDomain { get; init; } = string.Empty;

    [JsonPropertyName("addedTags")]
    public IReadOnlyList<string> AddedTags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "low";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

public sealed class PendingProposalDto
{
    [JsonPropertyName("opId")]
    public string OpId { get; init; } = string.Empty;

    [JsonPropertyName("changes")]
    public IReadOnlyList<ProposedChangeDto> Changes { get; init; } = Array.Empty<ProposedChangeDto>();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }
}

public sealed class ApplyProposalResultDto
{
    [JsonPropertyName("applied")]
    public int Applied { get; init; }

    [JsonPropertyName("undoToken")]
    public string? UndoToken { get; init; }
}

/// <summary>
/// The review half of the categorize flow — port of
/// <c>server/src/modules/tasks/categorize/apply.ts</c>.
///
/// <para>
/// Unlike every other bulk route, PROPOSING writes nothing to Task: the run is
/// staged as a <c>TaskBulkOp</c> in <c>proposed</c>, reviewed, and only then
/// committed — because a domain is required on every matter, so this always
/// overwrites an answer that already exists rather than filling a blank. Applying
/// goes through the same record, so <c>POST /me/tasks/undo/{token}</c> reverses a
/// categorize run with no special casing.
/// </para>
///
/// <para>Proposing itself belongs to the AI slice; with no key configured the
/// propose route is a 503 and no proposal is ever staged.</para>
/// </summary>
public sealed class CategorizeService
{
    private readonly IMongoCollection<TaskBulkOpDocument> _ops;
    private readonly IMongoCollection<TaskDocument> _tasks;

    public CategorizeService(IMongoDatabase database)
    {
        _ops = database.GetCollection<TaskBulkOpDocument>(MongoCollections.TaskBulkOps);
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
    }

    /// <summary>The user's one open proposal, if any.</summary>
    public async Task<PendingProposalDto?> GetPendingAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        var op = await _ops
            .Find(Builders<TaskBulkOpDocument>.Filter.And(
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.UserId, userId),
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Kind, "categorize"),
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Status, "proposed")))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return op is null ? null : await DescribeAsync(op, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commit the accepted subset.
    ///
    /// <para>
    /// The op's entries are NARROWED to what the user kept before anything is
    /// written. That is the load-bearing step: undo replays <c>entries[].prior</c>,
    /// so leaving a rejected row in the record would have undo "restore" a matter
    /// that was never touched — quietly reverting a domain the user had
    /// deliberately set.
    /// </para>
    /// </summary>
    public async Task<ApplyProposalResultDto> ApplyAsync(
        ObjectId userId,
        string opId,
        IReadOnlyList<string> acceptedTaskIds,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;
        var op = await LoadProposedAsync(userId, opId, cancellationToken).ConfigureAwait(false);

        var accepted = new HashSet<string>(acceptedTaskIds, StringComparer.Ordinal);
        var kept = op.Entries.Where(e => accepted.Contains(e.TaskId.ToString())).ToList();

        if (kept.Count == 0)
        {
            await SetStatusAsync(op.Id, "discarded", null, cancellationToken).ConfigureAwait(false);
            return new ApplyProposalResultDto { Applied = 0, UndoToken = null };
        }

        await _ops
            .UpdateOneAsync(
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Id, op.Id),
                Builders<TaskBulkOpDocument>.Update
                    .Set(o => o.Entries, kept)
                    .Set(o => o.Status, "applied")
                    .Set(o => o.AppliedAt, now)
                    .Set(o => o.UpdatedAt, now),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await _tasks
            .BulkWriteAsync(
                kept.Select(e => new UpdateOneModel<TaskDocument>(
                    // Re-asserting the live filter closes the window where the
                    // matter was trashed while the proposal sat on screen.
                    Builders<TaskDocument>.Filter.And(
                        Builders<TaskDocument>.Filter.Eq(t => t.Id, e.TaskId),
                        MongoRepositoryBase<TaskDocument>.LiveForUser(userId)),

                    // MUST be the kernel's shared translation — undo reads `prior`
                    // back through the same function, so a second implementation
                    // that treated BSON null differently would make categorize runs
                    // un-undoable in a way nothing would catch until someone tried.
                    new BsonDocumentUpdateDefinition<TaskDocument>(BulkService.ToMongoOps(e.Next)))),
                new BulkWriteOptions { IsOrdered = false },
                cancellationToken)
            .ConfigureAwait(false);

        return new ApplyProposalResultDto { Applied = kept.Count, UndoToken = op.Id.ToString() };
    }

    public async Task DiscardAsync(ObjectId userId, string opId, CancellationToken cancellationToken = default)
    {
        var op = await LoadProposedAsync(userId, opId, cancellationToken).ConfigureAwait(false);
        await SetStatusAsync(op.Id, "discarded", null, cancellationToken).ConfigureAwait(false);
    }

    private Task SetStatusAsync(ObjectId opId, string status, DateTime? appliedAt, CancellationToken cancellationToken)
    {
        var update = Builders<TaskBulkOpDocument>.Update
            .Set(o => o.Status, status)
            .Set(o => o.UpdatedAt, DateTime.UtcNow);

        if (appliedAt.HasValue)
        {
            update = update.Set(o => o.AppliedAt, appliedAt.Value);
        }

        return _ops.UpdateOneAsync(
            Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Id, opId),
            update,
            cancellationToken: cancellationToken);
    }

    private async Task<TaskBulkOpDocument> LoadProposedAsync(
        ObjectId userId,
        string opId,
        CancellationToken cancellationToken)
    {
        // A malformed id is the SAME 404 as a missing one — the client cannot tell,
        // and should not have to.
        if (!ObjectId.TryParse(opId, out var parsed))
        {
            throw AppException.NotFound("proposal_not_found", "That suggestion has expired.");
        }

        var op = await _ops
            .Find(Builders<TaskBulkOpDocument>.Filter.And(
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Id, parsed),
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.UserId, userId),
                Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Kind, "categorize")))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (op is null || op.Status != "proposed")
        {
            throw AppException.NotFound("proposal_not_found", "That suggestion has expired.");
        }

        return op;
    }

    /// <summary>
    /// The op stores only taskId/prior/next — enough to apply and to undo, but not
    /// enough to RENDER. Titles are joined back on read rather than denormalised
    /// into the op, because a title the user edited between proposing and reviewing
    /// should show as it is NOW, not as it was when the model saw it.
    /// </summary>
    private async Task<PendingProposalDto> DescribeAsync(
        TaskBulkOpDocument op,
        ObjectId userId,
        CancellationToken cancellationToken)
    {
        var ids = op.Entries.Select(e => e.TaskId).ToList();

        var tasks = await _tasks
            .Find(Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.In(t => t.Id, ids),
                MongoRepositoryBase<TaskDocument>.LiveForUser(userId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = tasks.ToDictionary(t => t.Id);

        var changes = new List<ProposedChangeDto>();
        foreach (var entry in op.Entries)
        {
            // Deleted or trashed since the proposal was made — drop it rather than
            // offering to refile something no longer in the list.
            if (!byId.TryGetValue(entry.TaskId, out var task))
            {
                continue;
            }

            var toDomain = entry.Next.TryGetValue("domain", out var d) && d.IsString ? d.AsString : task.Domain;

            var nextTags = entry.Next.TryGetValue("tags", out var t) && t.IsBsonArray
                ? t.AsBsonArray.Select(v => v.AsString).ToList()
                : task.Tags.ToList();

            changes.Add(new ProposedChangeDto
            {
                TaskId = entry.TaskId.ToString(),
                Title = task.Title,
                FromDomain = task.Domain,
                ToDomain = toDomain,
                AddedTags = nextTags.Where(tag => !task.Tags.Contains(tag)).ToList(),
                Confidence = entry.Confidence ?? "low",
                Reason = entry.Reason ?? string.Empty,
            });
        }

        return new PendingProposalDto
        {
            OpId = op.Id.ToString(),
            Changes = changes,
            CreatedAt = op.CreatedAt,
        };
    }
}
