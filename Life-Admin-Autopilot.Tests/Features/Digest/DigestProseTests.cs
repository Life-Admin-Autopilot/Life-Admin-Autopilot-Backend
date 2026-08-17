using Life_Admin_Autopilot.BLL.Features.Digest;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Digest;

/// <summary>
/// What the model returns is a SUGGESTION, and this is the gate it goes through
/// before it reaches the top of anyone's home screen. No database and no network —
/// these run everywhere, always.
/// </summary>
public sealed class DigestProseCleaningTests
{
    [Fact]
    public void keeps_a_plain_sentence_untouched()
    {
        const string sentence =
            "Today you're booking the North Coast tickets and chasing the airline refund.";

        Assert.Equal(sentence, DigestProseWriter.Clean(sentence));
    }

    [Fact]
    public void strips_the_fence_a_model_wraps_prose_in()
    {
        Assert.Equal(
            "Today you're renewing the car insurance.",
            DigestProseWriter.Clean("```\nToday you're renewing the car insurance.\n```"));
    }

    [Fact]
    public void unwraps_a_sentence_the_model_put_in_quotes()
    {
        // Both the straight and the typographic pair — a model that has been told
        // "reply with only the sentence" reaches for either.
        Assert.Equal("Today you're paying the vet.", DigestProseWriter.Clean("\"Today you're paying the vet.\""));
        Assert.Equal("Today you're paying the vet.", DigestProseWriter.Clean("“Today you're paying the vet.”"));
    }

    [Fact]
    public void collapses_a_list_into_one_line()
    {
        // The hero is a single paragraph. A model that ignored "no bullet points"
        // still must not put line breaks through the layout.
        Assert.Equal(
            "Today you have two things: the vet, and the refund.",
            DigestProseWriter.Clean("Today you have two things:\n  the vet,\n  and the refund."));
    }

    [Fact]
    public void rejects_a_paragraph()
    {
        // A model that answered with an essay has misunderstood the job, and the
        // computed count is a better headline than a truncated one.
        Assert.Null(DigestProseWriter.Clean(new string('a', 241)));
    }

    [Fact]
    public void rejects_nothing_at_all()
    {
        Assert.Null(DigestProseWriter.Clean("   "));
        Assert.Null(DigestProseWriter.Clean("```\n\n```"));
    }
}

/// <summary>
/// The queue's pending flag is a PROMISE TO THE CLIENT — the dashboard refetches
/// while it is true. Every case here is about that promise being kept: it goes true
/// only when a job is really queued, and false again however the job ends.
/// </summary>
public sealed class DigestProseQueueTests
{
    private static readonly ObjectId User = ObjectId.GenerateNewId();

    private const string Today = "2026-08-17";

    private static DigestProseJob Job(string sourceHash, string localDate = Today) => new(
        User,
        localDate,
        sourceHash,
        "en",
        new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc),
        new[] { new DigestPoolMatter("t1", "Book the North Coast tickets", "travel", null) });

    [Fact]
    public void a_queued_job_is_pending_until_it_completes()
    {
        var queue = new DigestProseQueue();
        var job = Job("hash-a");

        Assert.False(queue.IsPending(User, Today));

        Assert.True(queue.TryEnqueue(job));
        Assert.True(queue.IsPending(User, Today));

        queue.Complete(job);
        Assert.False(queue.IsPending(User, Today));
    }

    [Fact]
    public void completing_clears_the_flag_even_when_the_model_wrote_nothing()
    {
        // The worker reports completion from a finally, so this is the failure path
        // as well as the success one. If it did not clear here, a client would poll
        // for a write that is never coming.
        var queue = new DigestProseQueue();
        var job = Job("hash-a");

        queue.TryEnqueue(job);
        queue.Complete(job);

        Assert.False(queue.IsPending(User, Today));
    }

    [Fact]
    public void a_superseded_job_finishing_does_not_clear_its_successors_flag()
    {
        // The user edited a matter while the first sentence was being written. The
        // second job owns the day now; the first one landing must not tell the client
        // to stop waiting for it.
        var queue = new DigestProseQueue();
        var first = Job("hash-a");
        var second = Job("hash-b");

        queue.TryEnqueue(first);
        queue.TryEnqueue(second);

        queue.Complete(first);
        Assert.True(queue.IsPending(User, Today));

        queue.Complete(second);
        Assert.False(queue.IsPending(User, Today));
    }

    [Fact]
    public void one_users_day_says_nothing_about_another()
    {
        var queue = new DigestProseQueue();

        queue.TryEnqueue(Job("hash-a"));

        Assert.False(queue.IsPending(ObjectId.GenerateNewId(), Today));
        Assert.False(queue.IsPending(User, "2026-08-18"));
    }

    [Fact]
    public async Task jobs_come_back_out_in_the_order_they_went_in()
    {
        var queue = new DigestProseQueue();
        queue.TryEnqueue(Job("hash-a"));
        queue.TryEnqueue(Job("hash-b", localDate: "2026-08-18"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var drained = new List<string>();

        await foreach (var job in queue.ReadAllAsync(cts.Token))
        {
            drained.Add(job.SourceHash);
            if (drained.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(new[] { "hash-a", "hash-b" }, drained);
    }

    [Fact]
    public void a_full_queue_drops_the_write_and_says_so()
    {
        // Bounded and lossy on purpose — the sentence is the lowest-value thing the
        // server produces, and shedding it costs one plain headline for one day. The
        // return value is what stops the client polling for the dropped job.
        var queue = new DigestProseQueue();

        // Capacity is 256; every job here is a distinct day so none supersedes another.
        for (var i = 0; i < 256; i++)
        {
            Assert.True(queue.TryEnqueue(Job("hash", localDate: $"day-{i}")));
        }

        var dropped = Job("hash", localDate: "day-256");
        Assert.False(queue.TryEnqueue(dropped));
        Assert.False(queue.IsPending(User, "day-256"));
    }
}
