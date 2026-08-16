using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// <b>The measured SSE protocol, in one object.</b> Both streaming routes write
/// through this and neither touches <c>Response.Body</c> directly, because every
/// detail below was captured off the running reference rather than inferred, and a
/// second hand-rolled copy would drift.
///
/// <list type="number">
///   <item>
///     <b>Headers, in this order, after the status.</b> <c>Content-Type:
///     text/event-stream</c>, <c>Cache-Control: no-cache, no-transform</c>,
///     <c>Connection: keep-alive</c>, <c>X-Accel-Buffering: no</c>, then flush. The
///     twelve helmet headers are already on the response by the time this runs, and
///     <c>Transfer-Encoding: chunked</c> follows from leaving
///     <c>Content-Length</c> unset. <c>X-Accel-Buffering</c> is the one that matters
///     operationally: without it nginx buffers the whole stream and the live
///     typing effect disappears in production but not in development.
///   </item>
///   <item>
///     <b>Frames are exactly <c>data: {json}\n\n</c>.</b> No <c>event:</c>, no
///     <c>id:</c>, no <c>retry:</c> — the client is a hand-written reader that splits
///     on the blank line and <c>JSON.parse</c>s the remainder, so an <c>event:</c>
///     line it does not expect would land inside its buffer. Each frame is ONE
///     write; a frame split across two writes can be delivered as two chunks and the
///     reader would parse half a JSON object.
///   </item>
///   <item>
///     <b><c>type</c> is always the first key.</b> Node builds the payload as
///     <c>{ type: event.kind, ...event }</c>, so it is first by construction.
///   </item>
///   <item>
///     <b>A heartbeat every 25 s, as an SSE COMMENT:</b> <c>: ping &lt;epochMs&gt;\n\n</c>.
///     Comments are ignored by every SSE parser, which is exactly why a keep-alive
///     is written as one. It exists to outlive proxy idle timeouts (nginx defaults
///     to 60 s) and is cleared before the stream ends, never after.
///   </item>
/// </list>
///
/// <para>
/// <b>THE RULE.</b> <see cref="HasOpened"/> is the line. Before it, a failure is an
/// ordinary JSON HTTP error with a real status code. After it, the status is already
/// on the wire and the same failure must be a <c>{"type":"error"}</c> frame inside a
/// 200. Clients branch on <c>Content-Type</c>, so getting this backwards makes an
/// error either invisible or unparseable.
/// </para>
///
/// <para>
/// <b>Writes are serialised.</b> Node is single-threaded and cannot interleave the
/// heartbeat with a frame; here the timer runs on its own task, so every write takes
/// the same semaphore. Without it a ping can land in the middle of a
/// <c>data:</c> line.
/// </para>
/// </summary>
public sealed class AiSseWriter : IAsyncDisposable
{
    /// <summary><c>SSE_HEARTBEAT_MS</c>.</summary>
    public const int HeartbeatMilliseconds = 25_000;

    private static readonly byte[] FramePrefix = "data: "u8.ToArray();
    private static readonly byte[] FrameSuffix = "\n\n"u8.ToArray();

    private readonly HttpResponse _response;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _heartbeatStop = new();

    private Task? _heartbeat;
    private bool _disposed;

    public AiSseWriter(HttpResponse response, TimeSpan? heartbeatInterval = null, TimeProvider? time = null)
    {
        _response = response;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromMilliseconds(HeartbeatMilliseconds);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True once the headers have flushed. The before/after line.</summary>
    public bool HasOpened { get; private set; }

    /// <summary>
    /// Status, the four headers in order, flush, then start the heartbeat.
    ///
    /// <para>The heartbeat is started AFTER the flush on purpose: a ping written
    /// before the headers would itself flush them, out of order.</para>
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (HasOpened)
        {
            return;
        }

        _response.StatusCode = StatusCodes.Status200OK;

        // Set through the raw header collection, in the reference's order.
        // Content-Length stays unset so Kestrel chunks the body.
        _response.Headers["Content-Type"] = "text/event-stream";
        _response.Headers["Cache-Control"] = "no-cache, no-transform";
        _response.Headers["Connection"] = "keep-alive";
        _response.Headers["X-Accel-Buffering"] = "no";
        _response.ContentLength = null;

        await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        HasOpened = true;

        _heartbeat = RunHeartbeatAsync(_heartbeatStop.Token);
    }

    /// <summary>Write one event as a single <c>data: {json}\n\n</c> frame.</summary>
    public Task SendAsync(AiStreamEvent value, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(Serialize(value), cancellationToken);

    /// <summary>
    /// Write an arbitrary object as one frame.
    ///
    /// <para>
    /// For frames the route owns rather than the provider — today just
    /// <c>conversation</c>, which reports the thread the turn was filed under and so
    /// has no place in <see cref="AiStreamEvent"/>'s provider-facing vocabulary.
    /// Serialized with <see cref="AiStreamJson.Frame"/> for the same reason
    /// <see cref="Serialize"/> is: the response serializer drops nulls.
    /// </para>
    /// </summary>
    public Task SendRawAsync<T>(T frame, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(frame, AiStreamJson.Frame), cancellationToken);

    /// <summary>
    /// Serialize one event to its frame JSON. <c>type</c> first, then the payload in
    /// insertion order.
    ///
    /// <para>Uses <see cref="AiStreamJson.Frame"/>, NOT the response serializer — the
    /// latter drops nulls, and <c>tool_result</c> needs its unused half written as an
    /// explicit <c>null</c>.</para>
    /// </summary>
    public static byte[] Serialize(AiStreamEvent value)
    {
        using var buffer = new MemoryStream(256);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = AiStreamJson.Frame.Encoder,
            Indented = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Kind);

            foreach (var (name, member) in value.Payload)
            {
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, member, AiStreamJson.Frame);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task WriteFrameAsync(byte[] json, CancellationToken cancellationToken)
    {
        // One buffer, one write. See the class remarks — a frame must never be split.
        var frame = new byte[FramePrefix.Length + json.Length + FrameSuffix.Length];
        FramePrefix.CopyTo(frame, 0);
        json.CopyTo(frame, FramePrefix.Length);
        FrameSuffix.CopyTo(frame, FramePrefix.Length + json.Length);

        await WriteRawAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRawAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval, _time);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var epochMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
                await WriteRawAsync(
                        Encoding.UTF8.GetBytes($": ping {epochMs}\n\n"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The ordinary way a heartbeat ends.
        }
        catch (ObjectDisposedException)
        {
            // The response body went away first; the turn is over either way.
        }
        catch (IOException)
        {
            // Client hung up mid-ping. Not worth failing the turn over.
        }
    }

    /// <summary>
    /// Stops the heartbeat and lets the response end. Idempotent, and safe to call
    /// from a <c>finally</c> that also ran after an exception — Node clears its
    /// interval in exactly the same place, BEFORE <c>res.end()</c>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _heartbeatStop.CancelAsync().ConfigureAwait(false);

        if (_heartbeat is not null)
        {
            try
            {
                await _heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _heartbeatStop.Dispose();
        _writeLock.Dispose();
    }
}
