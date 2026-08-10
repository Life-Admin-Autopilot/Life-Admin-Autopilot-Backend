using System.Net;
using System.Text;
using Life_Admin_Autopilot.BLL.Features.IcsFeeds;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>Scripted transport: one canned response per URL, and a log of what was asked for.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes =
        new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = new();

    public ScriptedHandler Respond(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[url] = respond;
        return this;
    }

    public ScriptedHandler Status(string url, HttpStatusCode status) =>
        Respond(url, _ => new HttpResponseMessage(status));

    public ScriptedHandler Calendar(string url, string body, string? etag = null, string? lastModified = null) =>
        Respond(url, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/calendar"),
            };

            if (etag is not null)
            {
                response.Headers.TryAddWithoutValidation("ETag", etag);
            }

            if (lastModified is not null)
            {
                // Last-Modified is an ENTITY header, so it lands on Content.Headers —
                // `response.Headers` silently refuses it. Real servers put it there
                // too, which is why the fetcher looks in both collections.
                response.Content.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
            }

            return response;
        });

    public ScriptedHandler Redirect(string url, HttpStatusCode status, string? location) =>
        Respond(url, _ =>
        {
            var response = new HttpResponseMessage(status);
            if (location is not null)
            {
                response.Headers.TryAddWithoutValidation("Location", location);
            }

            return response;
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var key = request.RequestUri!.AbsoluteUri;
        if (!_routes.TryGetValue(key, out var respond))
        {
            throw new HttpRequestException($"No scripted response for {key}");
        }

        return Task.FromResult(respond(request));
    }
}

internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// Port of <c>modules/integrations/ics/fetchFeed.test.ts</c>, extended to cover the
/// redirect re-vetting the reference's own suite does not reach.
///
/// <para>
/// <b>The redirect tests are the point of this file.</b> The SSRF guard vets the
/// first hostname, but a publisher that passes can still 302 into private space. If
/// a future change re-enables automatic redirect following on the handler, every
/// other test here still passes and only <c>re_vets_every_redirect_hop</c> fails.
/// </para>
/// </summary>
public sealed class FeedFetcherTests
{
    private const string Feed = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task returns_unchanged_on_304_without_reading_a_body()
    {
        var handler = new ScriptedHandler().Status("https://feeds.example/a.ics", HttpStatusCode.NotModified);

        var result = await Fetch(handler, "https://feeds.example/a.ics", new FeedCacheState("\"v1\"", null));

        Assert.Equal(FeedFetchStatus.Unchanged, result.Status);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task replays_the_cache_validators_and_identifies_itself()
    {
        var handler = new ScriptedHandler().Status("https://feeds.example/a.ics", HttpStatusCode.NotModified);

        await Fetch(
            handler,
            "https://feeds.example/a.ics",
            new FeedCacheState("\"v1\"", "Wed, 21 Oct 2026 07:28:00 GMT"));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("\"v1\"", Header(sent, "if-none-match"));
        Assert.Equal("Wed, 21 Oct 2026 07:28:00 GMT", Header(sent, "if-modified-since"));
        Assert.Equal(FeedFetcher.UserAgent, Header(sent, "user-agent"));
        Assert.Equal(FeedFetcher.Accept, Header(sent, "accept"));
    }

    [Fact]
    public async Task omits_the_conditional_headers_on_a_first_poll()
    {
        var handler = new ScriptedHandler().Calendar("https://feeds.example/a.ics", Feed);

        await Fetch(handler, "https://feeds.example/a.ics");

        var sent = Assert.Single(handler.Requests);
        Assert.Null(Header(sent, "if-none-match"));
        Assert.Null(Header(sent, "if-modified-since"));
    }

    [Fact]
    public async Task returns_the_body_and_the_publishers_validators()
    {
        var handler = new ScriptedHandler()
            .Calendar("https://feeds.example/a.ics", Feed, "\"v2\"", "Wed, 21 Oct 2026 07:28:00 GMT");

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Ok, result.Status);
        Assert.Equal(Feed, result.Body);
        Assert.Equal("\"v2\"", result.Etag);
        Assert.Equal("Wed, 21 Oct 2026 07:28:00 GMT", result.LastModified);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task reports_a_retired_url_as_gone(HttpStatusCode status)
    {
        // 'gone' is terminal — a 404 does not start working again on its own, and the
        // user needs telling rather than being left with silently frozen events.
        var handler = new ScriptedHandler().Status("https://feeds.example/a.ics", status);

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Gone, result.Status);
        Assert.Equal("That feed no longer exists.", result.Reason);
    }

    [Fact]
    public async Task reports_any_other_failure_status_with_its_code()
    {
        var handler = new ScriptedHandler().Status("https://feeds.example/a.ics", HttpStatusCode.InternalServerError);

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Error, result.Status);
        Assert.Equal("That feed returned 500.", result.Reason);
    }

    [Fact]
    public async Task rejects_a_200_that_is_not_a_calendar()
    {
        // An expired or auth-gated feed URL commonly answers 200 with a login page.
        // Parsed as ICS that yields zero events, indistinguishable from "term has no
        // dates" — so every reminder would silently disappear.
        var handler = new ScriptedHandler().Respond(
            "https://feeds.example/a.ics",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Please sign in</body></html>"),
            });

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Error, result.Status);
        Assert.Equal("That address did not return a calendar.", result.Reason);
    }

    [Fact]
    public async Task sniffs_the_body_rather_than_the_content_type()
    {
        // Plenty of councils serve valid .ics as text/plain or
        // application/octet-stream, so the header is too unreliable to gate on.
        var handler = new ScriptedHandler().Respond(
            "https://feeds.example/a.ics",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed, Encoding.UTF8, "application/octet-stream"),
            });

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Ok, result.Status);
    }

    [Fact]
    public void only_sniffs_the_first_2048_characters()
    {
        Assert.False(FeedFetcher.LooksLikeICalendar(new string(' ', 2048) + "BEGIN:VCALENDAR"));
        Assert.True(FeedFetcher.LooksLikeICalendar(new string(' ', 2000) + "BEGIN:VCALENDAR"));
        Assert.True(FeedFetcher.LooksLikeICalendar("begin:vcalendar"));
    }

    // ---- redirects: the whole reason this fetcher exists -------------------

    [Fact]
    public async Task re_vets_every_redirect_hop()
    {
        // THE test. The first hostname is public and passes the guard; the redirect
        // target is the cloud metadata endpoint. An auto-following handler would
        // fetch it and hand back instance credentials as "calendar data".
        var handler = new ScriptedHandler()
            .Redirect("https://feeds.example/a.ics", HttpStatusCode.Found, "http://169.254.169.254/latest/meta-data")
            .Calendar("http://169.254.169.254/latest/meta-data", Feed);

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Error, result.Status);
        Assert.Equal(FeedUrlGuard.NotPubliclyReachable, result.Reason);

        // And it never opened the socket to the metadata endpoint.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task re_vets_the_scheme_of_a_redirect_hop()
    {
        var handler = new ScriptedHandler()
            .Redirect("https://feeds.example/a.ics", HttpStatusCode.MovedPermanently, "file:///etc/passwd");

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedUrlGuard.MustUseHttps, result.Reason);
    }

    [Fact]
    public async Task follows_a_relative_redirect_resolved_against_the_current_hop()
    {
        var handler = new ScriptedHandler()
            .Redirect("https://feeds.example/a.ics", HttpStatusCode.Found, "/moved/b.ics")
            .Calendar("https://feeds.example/moved/b.ics", Feed);

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal(FeedFetchStatus.Ok, result.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task refuses_a_redirect_with_no_location()
    {
        var handler = new ScriptedHandler().Redirect("https://feeds.example/a.ics", HttpStatusCode.Found, null);

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal("That feed sent an invalid redirect.", result.Reason);
    }

    [Fact]
    public async Task gives_up_after_three_hops()
    {
        var handler = new ScriptedHandler();
        for (var i = 0; i < 8; i += 1)
        {
            handler.Redirect($"https://feeds.example/{i}.ics", HttpStatusCode.Found, $"https://feeds.example/{i + 1}.ics");
        }

        var result = await Fetch(handler, "https://feeds.example/0.ics");

        Assert.Equal("That feed redirected too many times.", result.Reason);

        // Four requests: the original plus three followed hops.
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task still_answers_304_from_a_redirect_target()
    {
        var handler = new ScriptedHandler()
            .Redirect("https://feeds.example/a.ics", HttpStatusCode.Found, "https://feeds.example/b.ics")
            .Status("https://feeds.example/b.ics", HttpStatusCode.NotModified);

        var result = await Fetch(handler, "https://feeds.example/a.ics", new FeedCacheState("\"v1\"", null));

        Assert.Equal(FeedFetchStatus.Unchanged, result.Status);
        Assert.Equal("\"v1\"", Header(handler.Requests[1], "if-none-match"));
    }

    // ---- size ceiling ------------------------------------------------------

    [Fact]
    public async Task refuses_a_body_that_declares_itself_too_large()
    {
        var handler = new ScriptedHandler().Respond("https://feeds.example/a.ics", _ =>
        {
            var content = new StringContent(Feed);
            content.Headers.ContentLength = FeedFetcher.MaxFeedBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal("That feed is too large.", result.Reason);
    }

    [Fact]
    public async Task refuses_a_body_that_exceeds_the_ceiling_while_streaming()
    {
        // Content-Length is a hint, not a promise. A publisher that under-declares
        // must still not be able to exhaust memory.
        var handler = new ScriptedHandler().Respond("https://feeds.example/a.ics", _ =>
        {
            var content = new StreamContent(new EndlessStream());
            content.Headers.ContentLength = 10;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal("That feed is too large.", result.Reason);
    }

    // ---- transport failures ------------------------------------------------

    [Fact]
    public async Task reports_an_unreachable_feed()
    {
        var handler = new ScriptedHandler().Respond(
            "https://feeds.example/a.ics",
            _ => throw new HttpRequestException("connection refused"));

        var result = await Fetch(handler, "https://feeds.example/a.ics");

        Assert.Equal("That feed could not be reached.", result.Reason);
    }

    [Fact]
    public async Task refuses_an_unsafe_url_before_opening_a_socket()
    {
        var handler = new ScriptedHandler();

        var fetcher = new FeedFetcher(
            new SingleClientFactory(handler),
            new FeedUrlGuard(new StubDnsResolver().Resolving("127.0.0.1", "127.0.0.1")));

        var result = await fetcher.FetchAsync("http://127.0.0.1/a.ics");

        Assert.Equal(FeedUrlGuard.NotPubliclyReachable, result.Reason);
        Assert.Empty(handler.Requests);
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<FeedFetchResult> Fetch(ScriptedHandler handler, string url, FeedCacheState cache = default)
    {
        var dns = new StubDnsResolver()
            .Resolving("feeds.example", "93.184.216.34")
            .Resolving("169.254.169.254", "169.254.169.254");

        var fetcher = new FeedFetcher(new SingleClientFactory(handler), new FeedUrlGuard(dns));
        return fetcher.FetchAsync(url, cache);
    }

    /// <summary>
    /// Reads the RAW header value. <c>TryGetValues</c> would hand back a User-Agent
    /// pre-split into product and comment tokens, which says nothing about what goes
    /// on the wire.
    /// </summary>
    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.NonValidated.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : null;

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)'A');
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'A');
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
