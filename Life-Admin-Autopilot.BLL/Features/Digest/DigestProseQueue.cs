using System.Collections.Concurrent;
using System.Threading.Channels;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>One matter, reduced to what a sentence about the day can be built from.</summary>
/// <param name="Id">Stringified <c>_id</c>. Never shown to the user — see the ids rule in
/// <see cref="DigestProseWriter"/>.</param>
/// <param name="Title">The matter's title, verbatim.</param>
/// <param name="Domain">Its domain label, verbatim.</param>
/// <param name="DueAt">UTC deadline. Null is possible in principle; the pool query only
/// matches dated matters, so in practice it never is.</param>
public sealed record DigestPoolMatter(string Id, string Title, string Domain, DateTime? DueAt);

/// <summary>
/// One request to write the day's sentence. SELF-CONTAINED on purpose: the worker
/// that runs it holds no request, no user and no clock of its own, so everything
/// the model call and the guarded write-back need travels with the job.
/// </summary>
/// <param name="SourceHash">The fingerprint the facts were read at. The write-back is
/// conditional on it, so a sentence about a state the user has already moved past is
/// discarded rather than shown.</param>
public sealed record DigestProseJob(
    ObjectId UserId,
    string LocalDate,
    string SourceHash,
    string Locale,
    DateTime Now,
    IReadOnlyList<DigestPoolMatter> Pool);

/// <summary>
/// The hand-off between the dashboard's read and the model call that improves its
/// one sentence.
///
/// <para>
/// <b>Why this is a queue and not an await.</b> Every count in the digest is already
/// in hand the moment the aggregation returns. Awaiting a language model before
/// answering would make the user wait on prose for numbers that are finished — and
/// the wait would land on exactly the visits that matter, because a cache miss means
/// they just changed something. So the request answers with the computed headline
/// and drops a job here; <c>DigestProseWorker</c> writes the better sentence into
/// the cached row and the next read serves it. A plain sentence immediately beats a
/// nicer one late.
/// </para>
///
/// <para>
/// <b><see cref="IsPending"/> is what stops the client polling forever.</b> The
/// dashboard refetches while the response says a sentence is coming. That claim has
/// to go false on failure as well as on success, which is why completion is reported
/// through <see cref="Complete"/> from a <c>finally</c> rather than inferred from the
/// stored row: a model that answered nothing patches nothing, and a client watching
/// only the headline would wait on a write that is never coming.
/// </para>
///
/// <para>
/// <b>Bounded and lossy.</b> A full queue drops the write rather than growing without
/// limit or blocking the dashboard's response — the cost of a drop is one plain
/// headline for one day.
/// </para>
///
/// <para>
/// <b>Single-process.</b> Pending state lives in memory, so behind more than one
/// instance a client may stop polling while another instance is still writing. The
/// consequence is a stale-by-one-visit headline, never a wrong one; the write-back
/// itself is guarded by <c>sourceHash</c> in the database and is safe from any
/// instance.
/// </para>
/// </summary>
public sealed class DigestProseQueue
{
    /// <summary>
    /// Room for a burst of dashboard loads. Past this the sentence is skipped, which
    /// is the correct thing to shed under load: nothing else in the digest depends on
    /// it.
    /// </summary>
    private const int Capacity = 256;

    private readonly Channel<DigestProseJob> _jobs = Channel.CreateBounded<DigestProseJob>(
        new BoundedChannelOptions(Capacity)
        {
            // `Wait`, NOT `DropWrite`, even though dropping is exactly what a full
            // queue should do here. `DropWrite` discards the item and still reports
            // success, so `TryWrite` returns true for a job that will never run — and
            // the pending flag below would then stay set forever, leaving the
            // dashboard refetching for a sentence nobody is writing. Under `Wait`
            // only the async writer blocks; `TryWrite` returns false when full, which
            // is the honest answer this needs.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    /// <summary>
    /// <c>userId|localDate</c> to the <c>sourceHash</c> of the newest job queued for
    /// it. Keyed by DAY rather than by fingerprint so the dashboard can ask "is a
    /// sentence coming for today?" without knowing which build it would describe.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <returns>False when the queue is full and the job was dropped.</returns>
    public bool TryEnqueue(DigestProseJob job)
    {
        var key = KeyFor(job.UserId, job.LocalDate);

        // Recorded BEFORE the write, so a job can never be in the channel without
        // being pending — the ordering a reader on another thread would otherwise
        // race. Overwriting is deliberate: a second edit in the same day supersedes
        // the first, and the newer fingerprint is the one worth waiting for.
        _pending[key] = job.SourceHash;

        if (_jobs.Writer.TryWrite(job))
        {
            return true;
        }

        // Only retract the claim if it is still ours. A job queued in between owns
        // the day now, and clearing its flag would stop the client polling for a
        // sentence that is genuinely on its way.
        _pending.TryRemove(new KeyValuePair<string, string>(key, job.SourceHash));
        return false;
    }

    /// <summary>Is a sentence being written for this user's day right now?</summary>
    public bool IsPending(ObjectId userId, string localDate) =>
        _pending.ContainsKey(KeyFor(userId, localDate));

    /// <summary>
    /// Report a job finished — SUCCEEDED OR FAILED, both matter. Conditional on the
    /// fingerprint for the same reason the enqueue is: a superseded job finishing
    /// must not clear the flag its successor is holding.
    /// </summary>
    public void Complete(DigestProseJob job) =>
        _pending.TryRemove(
            new KeyValuePair<string, string>(KeyFor(job.UserId, job.LocalDate), job.SourceHash));

    public IAsyncEnumerable<DigestProseJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _jobs.Reader.ReadAllAsync(cancellationToken);

    private static string KeyFor(ObjectId userId, string localDate) => $"{userId}|{localDate}";
}
