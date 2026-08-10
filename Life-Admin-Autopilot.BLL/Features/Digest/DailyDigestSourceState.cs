using System.Security.Cryptography;
using System.Text;
using Life_Admin_Autopilot.BLL.Kernel.Json;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>Count plus newest <c>updatedAt</c> for one collection.</summary>
public readonly record struct CountAndStamp(int N, DateTime? Updated);

/// <summary>
/// The cache fingerprint, and the two counts that fall out of computing it.
/// </summary>
/// <param name="Hash">sha256 over the whole state summary.</param>
/// <param name="NeedsInput">Visible-open clarifications. Free from the tally.</param>
/// <param name="ScansAwaitingReview">Scans in <c>ready_for_review</c>. Free from the tally.</param>
public readonly record struct DailyDigestSourceState(string Hash, int NeedsInput, int ScansAwaitingReview);

/// <summary>
/// Port of <c>readSourceState</c> from <c>server/src/modules/tasks/dailyDigest.ts</c>.
///
/// <para>
/// What the cache is keyed on besides the date: a cheap summary of every document
/// the digest reads. Count plus newest-updated per collection catches creates,
/// edits, completions and soft deletes — anything that leaves the matched set moves
/// the count, and anything that changes in place moves the stamp. Within one local
/// day nothing else can move a figure: the day boundaries, the slipping cutoff and
/// the busiest-day window are all fixed relative to a local midnight that is
/// already part of the key. Identical fingerprint plus identical date therefore
/// means identical numbers.
/// </para>
///
/// <para>
/// <b>This is the load-bearing dependency on other slices stamping <c>updatedAt</c>.</b>
/// Mongoose adds it to every write on a <c>timestamps: true</c> model, including
/// into the <c>$set</c> of an update the application code wrote by hand. The .NET
/// driver adds nothing. Any slice that mutates <c>tasks</c>, <c>clarifications</c>
/// or <c>scanneddocuments</c> without setting <c>updatedAt</c> itself leaves this
/// fingerprint unmoved, and the dashboard then serves a digest that predates the
/// user's own edit until the count happens to change too. It is not detectable from
/// this file — only from a seeded write-then-read differential.
/// </para>
/// </summary>
public sealed class DailyDigestSourceReader
{
    private readonly IMongoDatabase _database;

    public DailyDigestSourceReader(IMongoDatabase database)
    {
        _database = database;
    }

    public async Task<DailyDigestSourceState> ReadAsync(
        ObjectId userId,
        string localDate,
        DateTime now,
        string locale,
        CancellationToken cancellationToken = default)
    {
        var tasksTask = TallyAsync(
            _database.GetCollection<TaskDocument>(MongoCollections.Tasks),
            MongoRepositoryBase<TaskDocument>.LiveForUser(userId),
            cancellationToken);

        // VisibleOpen(), NOT `status: 'open'`, and composed from the kernel rather
        // than hand-written — a question the user skipped is deferred, not
        // outstanding. `now` is threaded through rather than left to a wall-clock
        // default: the digest can be built with an injected clock, and judging a
        // deferral against a different instant would make this count disagree with
        // /me/tasks/counts for the same request.
        var clarificationsTask = TallyAsync(
            _database.GetCollection<ClarificationDocument>(MongoCollections.Clarifications),
            Builders<ClarificationDocument>.Filter.And(
                MongoRepositoryBase<ClarificationDocument>.UserScoped(userId),
                MongoRepositoryBase<ClarificationDocument>.VisibleOpen(now)),
            cancellationToken);

        // NOTE the ABSENT `reviewedAt` guard. See DailyDigestService — the resulting
        // disagreement with /me/tasks/counts is a ported bug, not an oversight.
        var scansTask = TallyAsync(
            _database.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments),
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", userId),
                Builders<BsonDocument>.Filter.Eq("status", "ready_for_review")),
            cancellationToken);

        await Task.WhenAll(tasksTask, clarificationsTask, scansTask).ConfigureAwait(false);

        var tasks = await tasksTask.ConfigureAwait(false);
        var clarifications = await clarificationsTask.ConfigureAwait(false);
        var scans = await scansTask.ConfigureAwait(false);

        // The locale belongs IN the fingerprint, not beside it: switching language
        // changes what the row should say without touching a single matter, so
        // nothing else here would move and the old sentence would be served all day.
        var parts = string.Join(
            '|',
            localDate,
            locale,
            Render(tasks),
            Render(clarifications),
            Render(scans));

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(parts)));

        return new DailyDigestSourceState(hash, clarifications.N, scans.N);
    }

    /// <summary>
    /// <c>`${n}:${updated ? updated.toISOString() : '-'}`</c>. The ISO form is JS's
    /// three-fractional-digit one, so the fingerprint string is byte-identical to
    /// the reference server's for the same data.
    /// </summary>
    internal static string Render(CountAndStamp stamp) =>
        $"{stamp.N}:{(stamp.Updated is { } at ? JsIsoDateTimeConverter.ToIso(at) : "-")}";

    private static async Task<CountAndStamp> TallyAsync<TDocument>(
        IMongoCollection<TDocument> collection,
        FilterDefinition<TDocument> match,
        CancellationToken cancellationToken)
    {
        var row = await collection
            .Aggregate()
            .Match(match)
            .Group<BsonDocument>(new BsonDocument
            {
                ["_id"] = BsonNull.Value,
                ["n"] = new BsonDocument("$sum", 1),
                ["updated"] = new BsonDocument("$max", "$updatedAt"),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return new CountAndStamp(0, null);
        }

        var n = row.TryGetValue("n", out var count) && count.IsNumeric ? count.ToInt32() : 0;

        // `row.updated instanceof Date` — a matched set whose members never stored an
        // updatedAt yields BSON null here, which is not a date and renders as '-'.
        var updated = row.TryGetValue("updated", out var stamp) && stamp.IsValidDateTime
            ? stamp.ToUniversalTime()
            : (DateTime?)null;

        return new CountAndStamp(n, updated);
    }
}
