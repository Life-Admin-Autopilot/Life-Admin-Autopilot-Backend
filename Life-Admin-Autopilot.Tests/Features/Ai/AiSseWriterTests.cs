using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot_Backend.Features.Ai;
using Microsoft.AspNetCore.Http;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The SSE writer against a real <see cref="HttpResponse"/> with a memory body — the
/// frame format, the header block, and the heartbeat, all directly observable.
///
/// <para>
/// These assertions ARE the measured contract: every value below was captured from
/// the running Node reference, and the shipped chat UI is already built against it.
/// </para>
/// </summary>
public sealed class AiSseWriterTests
{
    // ---- frame format -------------------------------------------------------

    [Fact]
    public async Task writes_exactly_data_json_and_two_newlines()
    {
        var (context, body) = Context();
        await using var sse = new AiSseWriter(context.Response);

        await sse.OpenAsync();
        await sse.SendAsync(AiStreamEvents.Token("hi"));

        // No `event:`, no `id:`, no `retry:`. The client is a hand-written reader
        // that splits on the blank line and JSON.parses the remainder.
        Assert.Equal("data: {\"type\":\"token\",\"text\":\"hi\"}\n\n", Text(body));
    }

    [Fact]
    public async Task puts_type_first_in_every_frame()
    {
        foreach (var value in new[]
                 {
                     AiStreamEvents.Sources(Array.Empty<AiStreamSource>()),
                     AiStreamEvents.Token("x"),
                     AiStreamEvents.ToolCall("c", "queryTasks", null, false),
                     AiStreamEvents.ToolResult("c", null, null),
                     AiStreamEvents.Done(),
                     AiStreamEvents.Error("code", "message"),
                     AiStreamEvents.Quota("free", Array.Empty<AiQuotaStatusDto>()),
                 })
        {
            var json = Encoding.UTF8.GetString(AiSseWriter.Serialize(value));

            // Node builds `{ type: event.kind, ...event }`, so `type` is first by
            // construction. Some clients read it positionally.
            Assert.StartsWith($"{{\"type\":\"{value.Kind}\"", json);
        }
    }

    [Fact]
    public void keeps_both_tool_result_members_with_the_unused_one_null()
    {
        var succeeded = Encoding.UTF8.GetString(
            AiSseWriter.Serialize(AiStreamEvents.ToolResult("c", new Dictionary<string, object?> { ["ok"] = true }, null)));
        var failed = Encoding.UTF8.GetString(
            AiSseWriter.Serialize(AiStreamEvents.ToolResult("c", null, "declined")));

        // The response serializer drops nulls (Mongoose omits unset fields), which
        // would make every failed call read as a success — this frame must NOT use
        // those options.
        Assert.Equal("""{"type":"tool_result","callId":"c","result":{"ok":true},"error":null}""", succeeded);
        Assert.Equal("""{"type":"tool_result","callId":"c","result":null,"error":"declined"}""", failed);
    }

    [Fact]
    public void writes_usage_as_an_empty_object_not_null()
    {
        // The decline path and the not-configured continuation both send `{}`.
        Assert.Equal(
            """{"type":"done","usage":{}}""",
            Encoding.UTF8.GetString(AiSseWriter.Serialize(AiStreamEvents.Done())));
    }

    [Fact]
    public void does_not_escape_non_ascii_inside_a_token()
    {
        var json = Encoding.UTF8.GetString(AiSseWriter.Serialize(AiStreamEvents.Token("موعد الطبيب")));

        // Express's res.json() does not escape non-ASCII. Escaping would turn every
        // Arabic answer into \uXXXX mid-stream.
        Assert.Contains("موعد الطبيب", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public async Task never_splits_a_frame_across_two_writes()
    {
        var (context, body) = Context();
        var recording = new RecordingBody();
        context.Response.Body = recording;

        await using var sse = new AiSseWriter(context.Response);
        await sse.OpenAsync();
        await sse.SendAsync(AiStreamEvents.Token(new string('x', 4096)));

        // A frame delivered as two chunks lets the client parse half a JSON object.
        var frames = recording.Writes.Where(w => w.Length > 0).ToList();
        Assert.Single(frames);
        Assert.StartsWith("data: ", Encoding.UTF8.GetString(frames[0]));
        Assert.EndsWith("\n\n", Encoding.UTF8.GetString(frames[0]));

        GC.KeepAlive(body);
    }

    // ---- the header block ---------------------------------------------------

    [Fact]
    public async Task sets_the_four_headers_in_the_reference_order()
    {
        var (context, _) = Context();
        await using var sse = new AiSseWriter(context.Response);

        await sse.OpenAsync();

        Assert.Equal(
            new[] { "Content-Type", "Cache-Control", "Connection", "X-Accel-Buffering" },
            context.Response.Headers.Keys.ToArray());

        Assert.Equal("text/event-stream", context.Response.Headers["Content-Type"]);
        Assert.Equal("no-cache, no-transform", context.Response.Headers["Cache-Control"]);
        Assert.Equal("keep-alive", context.Response.Headers["Connection"]);

        // Without this nginx buffers the whole stream: the live typing effect works
        // in development and silently disappears in production.
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public async Task leaves_content_length_unset_so_the_body_is_chunked()
    {
        var (context, _) = Context();
        await using var sse = new AiSseWriter(context.Response);

        await sse.OpenAsync();

        Assert.Null(context.Response.ContentLength);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task flips_has_opened_only_once_the_headers_are_out()
    {
        var (context, _) = Context();
        await using var sse = new AiSseWriter(context.Response);

        // THE RULE: before this line a failure is a JSON HTTP error with a real
        // status; after it, the status is committed and the same failure has to be
        // an error frame inside the 200.
        Assert.False(sse.HasOpened);
        await sse.OpenAsync();
        Assert.True(sse.HasOpened);
    }

    [Fact]
    public async Task opening_twice_does_not_write_a_second_header_block()
    {
        var (context, body) = Context();
        await using var sse = new AiSseWriter(context.Response);

        await sse.OpenAsync();
        await sse.OpenAsync();

        Assert.Empty(Text(body));
    }

    // ---- heartbeat ----------------------------------------------------------

    [Fact]
    public void keeps_the_measured_interval_as_the_default()
    {
        // 25s, from the reference. The tests below run a short interval so they do
        // not take half a minute, so this is the only assertion covering the value
        // that actually ships.
        Assert.Equal(25_000, AiSseWriter.HeartbeatMilliseconds);
    }

    [Fact]
    public async Task pings_as_an_sse_comment_carrying_epoch_milliseconds()
    {
        var (context, body) = Context();
        await using var sse = new AiSseWriter(context.Response, TimeSpan.FromMilliseconds(30));

        await sse.OpenAsync();
        await WaitUntil(() => Text(body).Contains(": ping"));

        var ping = Text(body).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[0];

        // A COMMENT, not a frame: every SSE parser ignores a line starting with `:`,
        // which is exactly why a keep-alive is written as one.
        Assert.StartsWith(": ping ", ping);
        Assert.True(long.TryParse(ping[": ping ".Length..], out var epochMs));
        Assert.True(epochMs > 1_600_000_000_000, "the ping carries epoch milliseconds, not seconds");
    }

    [Fact]
    public async Task keeps_pinging_until_the_stream_ends()
    {
        var (context, body) = Context();
        var sse = new AiSseWriter(context.Response, TimeSpan.FromMilliseconds(30));

        await sse.OpenAsync();
        await WaitUntil(() => Count(Text(body), ": ping") >= 2);

        // Cleared in the finally, BEFORE the stream ends — a timer that outlives the
        // response writes to a disposed body.
        await sse.DisposeAsync();
        var afterDispose = Count(Text(body), ": ping");

        await Task.Delay(120);
        Assert.Equal(afterDispose, Count(Text(body), ": ping"));
    }

    [Fact]
    public async Task does_not_interleave_a_ping_with_a_frame()
    {
        var (context, body) = Context();
        await using var sse = new AiSseWriter(context.Response, TimeSpan.FromMilliseconds(1));

        await sse.OpenAsync();

        for (var i = 0; i < 200; i++)
        {
            await sse.SendAsync(AiStreamEvents.Token("abcdefghij"));
        }

        // Node is single-threaded and cannot interleave; here the timer runs on its
        // own task, so every write takes the same lock. Without it a ping lands in
        // the middle of a data: line and the client's parse fails.
        foreach (var record in Text(body).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(
                record.StartsWith(": ping ", StringComparison.Ordinal)
                || record == """data: {"type":"token","text":"abcdefghij"}""",
                $"a record was corrupted by an interleaved write: {record}");
        }
    }

    [Fact]
    public async Task disposing_without_opening_is_harmless()
    {
        var (context, _) = Context();
        var sse = new AiSseWriter(context.Response);

        // The route's finally runs even when the failure happened before the flush.
        await sse.DisposeAsync();
        await sse.DisposeAsync();
    }

    // ---- helpers ------------------------------------------------------------

    private static (HttpContext Context, MemoryStream Body) Context()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return (context, body);
    }

    private static string Text(Stream body)
    {
        lock (body)
        {
            var buffer = ((MemoryStream)body).ToArray();
            return Encoding.UTF8.GetString(buffer);
        }
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "the writer never produced the expected output");
    }

    /// <summary>Records each <c>WriteAsync</c> separately, so a split frame is visible.</summary>
    private sealed class RecordingBody : MemoryStream
    {
        public List<byte[]> Writes { get; } = new();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.ToArray());
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
