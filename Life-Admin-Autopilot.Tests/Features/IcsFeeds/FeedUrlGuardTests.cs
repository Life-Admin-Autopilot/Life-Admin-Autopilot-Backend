using System.Net;
using System.Net.Sockets;
using Life_Admin_Autopilot.BLL.Features.IcsFeeds;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>
/// A resolver that answers from a table, so the SSRF rules can be driven without a
/// network — which is the whole reason the seam exists. The parity harness cannot
/// reach this code at all (it runs offline and the reference refuses the same URLs
/// for the same reasons), so these tests are the only coverage this guard has.
/// </summary>
public sealed class StubDnsResolver : IFeedDnsResolver
{
    private readonly Dictionary<string, IReadOnlyList<IPAddress>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Queried { get; } = new();

    public StubDnsResolver Resolving(string host, params string[] addresses)
    {
        _answers[host] = addresses.Select(IPAddress.Parse).ToList();
        return this;
    }

    /// <summary>An NXDOMAIN-shaped answer: the resolver throws.</summary>
    public StubDnsResolver Failing(string host)
    {
        _answers[host] = null!;
        return this;
    }

    /// <summary>A resolver that returns successfully but with nothing in it.</summary>
    public StubDnsResolver Empty(string host)
    {
        _answers[host] = Array.Empty<IPAddress>();
        return this;
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        Queried.Add(host);

        if (!_answers.TryGetValue(host, out var answer) || answer is null)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return Task.FromResult(answer);
    }
}

/// <summary>
/// Port of <c>modules/integrations/ics/feedUrl.test.ts</c>, plus the cases that file
/// could not reach.
///
/// <para>
/// This is the highest-risk code in the slice: it is the only thing standing between
/// "subscribe to my school calendar" and an authenticated GET of
/// <c>http://169.254.169.254/</c> from inside the deployment. Every message asserted
/// here is also API contract — the subscribe route re-emits it verbatim as the
/// <c>unsafe_feed_url</c> message.
/// </para>
/// </summary>
public sealed class FeedUrlGuardTests
{
    // ---- normalizeFeedUrl --------------------------------------------------

    [Fact]
    public void rewrites_webcal_to_https()
    {
        // webcal: is https: wearing a hat. Every publisher emits it.
        Assert.Equal(
            "https://school.example/terms.ics",
            FeedUrlGuard.NormalizeFeedUrl("webcal://school.example/terms.ics").AbsoluteUri);
    }

    [Fact]
    public void rewrites_webcal_case_insensitively()
    {
        Assert.Equal(
            "https://school.example/terms.ics",
            FeedUrlGuard.NormalizeFeedUrl("WEBCAL://school.example/terms.ics").AbsoluteUri);
    }

    [Fact]
    public void trims_surrounding_whitespace_before_parsing()
    {
        Assert.Equal(
            "https://school.example/terms.ics",
            FeedUrlGuard.NormalizeFeedUrl("  https://school.example/terms.ics  ").AbsoluteUri);
    }

    [Fact]
    public void accepts_plain_https_and_plain_http()
    {
        Assert.Equal("https", FeedUrlGuard.NormalizeFeedUrl("https://council.example/bins.ics").Scheme);
        Assert.Equal("http", FeedUrlGuard.NormalizeFeedUrl("http://council.example/bins.ics").Scheme);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a.ics")]
    [InlineData("gopher://example.com/")]
    [InlineData("data:text/calendar,BEGIN:VCALENDAR")]
    public void rejects_schemes_that_are_not_http_or_https(string bad)
    {
        // file: would read the server's own disk; gopher:/ftp: are classic SSRF
        // smuggling vectors.
        var error = Assert.Throws<UnsafeFeedUrlException>(() => FeedUrlGuard.NormalizeFeedUrl(bad));
        Assert.Equal(FeedUrlGuard.MustUseHttps, error.Message);
    }

    [Theory]
    [InlineData("https://user:pass@example.com/a.ics")]
    [InlineData("https://user@example.com/a.ics")]
    [InlineData("webcal://user:pass@example.com/a.ics")]
    public void rejects_embedded_credentials(string bad)
    {
        var error = Assert.Throws<UnsafeFeedUrlException>(() => FeedUrlGuard.NormalizeFeedUrl(bad));
        Assert.Equal(FeedUrlGuard.NoCredentials, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://")]
    public void rejects_unparseable_input(string bad)
    {
        var error = Assert.Throws<UnsafeFeedUrlException>(() => FeedUrlGuard.NormalizeFeedUrl(bad));
        Assert.Equal(FeedUrlGuard.NotAValidUrl, error.Message);
    }

    [Theory]
    // Captured from the live reference: a bare origin gains a trailing slash, so the
    // STORED url differs from what the client sent — and the (userId, url)
    // uniqueness check runs on this form.
    [InlineData("HTTPS://Example.COM", "https://example.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    // Percent-encoding, query and FRAGMENT all survive. The reference stores the
    // fragment too; dropping it would split one subscription into two rows.
    [InlineData("webcal://example.com/a%20b.ics?x=1&y=2#frag", "https://example.com/a%20b.ics?x=1&y=2#frag")]
    // The default port is dropped and dot segments are collapsed, so two spellings
    // of the same feed de-duplicate onto one subscription rather than two.
    [InlineData("https://example.com:443/x.ics", "https://example.com/x.ics")]
    [InlineData("https://EXAMPLE.com/A/../B.ics", "https://example.com/B.ics")]
    public void normalises_exactly_as_the_reference_does(string input, string expected) =>
        Assert.Equal(expected, FeedUrlGuard.NormalizeFeedUrl(input).AbsoluteUri);

    // ---- isPrivateAddress, IPv4 -------------------------------------------

    [Fact]
    public void blocks_the_cloud_metadata_endpoint()
    {
        // The single most valuable SSRF target: instance credentials.
        Assert.True(FeedUrlGuard.IsPrivateAddress("169.254.169.254", 4));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("0.0.0.0")]
    public void blocks_loopback_and_rfc1918_ranges(string ip) =>
        Assert.True(FeedUrlGuard.IsPrivateAddress(ip, 4));

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    public void blocks_cgnat_benchmarking_multicast_and_reserved_space(string ip) =>
        Assert.True(FeedUrlGuard.IsPrivateAddress(ip, 4));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("193.168.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    public void allows_genuinely_public_addresses(string ip) =>
        Assert.False(FeedUrlGuard.IsPrivateAddress(ip, 4));

    [Fact]
    public void does_not_let_a_near_miss_through()
    {
        // 172.16/12 has fiddly bounds — 172.15 and 172.32 are public, 172.16-31 are
        // not. An off-by-one here is a real hole.
        Assert.False(FeedUrlGuard.IsPrivateAddress("172.15.255.255", 4));
        Assert.True(FeedUrlGuard.IsPrivateAddress("172.16.0.0", 4));
        Assert.True(FeedUrlGuard.IsPrivateAddress("172.31.255.255", 4));
        Assert.False(FeedUrlGuard.IsPrivateAddress("172.32.0.0", 4));
    }

    [Theory]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.0.1")]
    [InlineData("not.an.ip.address")]
    [InlineData("")]
    public void fails_closed_on_anything_it_cannot_read_as_four_octets(string ip) =>
        // "I could not parse this" must never mean "so go and fetch it".
        Assert.True(FeedUrlGuard.IsPrivateAddress(ip, 4));

    // ---- isPrivateAddress, IPv6 -------------------------------------------

    [Theory]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("FE80::1")]
    [InlineData("feb0::1")]
    public void blocks_loopback_unspecified_ula_and_link_local(string ip) =>
        Assert.True(FeedUrlGuard.IsPrivateAddress(ip, 6));

    [Theory]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void unwraps_ipv4_mapped_addresses_instead_of_waving_them_through(string ip) =>
        // ::ffff:169.254.169.254 reaches the metadata endpoint. Treating it as an
        // opaque v6 string would bypass every v4 rule.
        Assert.True(FeedUrlGuard.IsPrivateAddress(ip, 6));

    [Fact]
    public void allows_a_public_ipv4_mapped_address()
    {
        Assert.False(FeedUrlGuard.IsPrivateAddress("::ffff:8.8.8.8", 6));
        Assert.False(FeedUrlGuard.IsPrivateAddress("2606:4700:4700::1111", 6));
    }

    [Fact]
    public void classifies_a_resolved_ipaddress_by_its_own_family()
    {
        // The overload the resolver path actually uses. An IPv4-mapped IPAddress
        // reports the v6 family and must still be unwrapped.
        Assert.True(FeedUrlGuard.IsPrivateAddress(IPAddress.Parse("169.254.169.254")));
        Assert.True(FeedUrlGuard.IsPrivateAddress(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.False(FeedUrlGuard.IsPrivateAddress(IPAddress.Parse("8.8.8.8")));
    }

    // ---- assertPublicFeedUrl ----------------------------------------------

    [Fact]
    public async Task accepts_a_host_whose_every_record_is_public()
    {
        var guard = new FeedUrlGuard(new StubDnsResolver().Resolving("school.example", "93.184.216.34", "2606:2800::1"));

        var url = await guard.PrepareFeedUrlAsync("https://school.example/terms.ics");

        Assert.Equal("https://school.example/terms.ics", url.AbsoluteUri);
    }

    [Fact]
    public async Task refuses_a_host_where_ANY_record_is_private()
    {
        // A hostname with one public and one private A record is an ATTACK, not a
        // misconfiguration — the loser of that race is the private one being reached.
        var guard = new FeedUrlGuard(new StubDnsResolver().Resolving("split.example", "93.184.216.34", "169.254.169.254"));

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => guard.PrepareFeedUrlAsync("https://split.example/a.ics"));

        Assert.Equal(FeedUrlGuard.NotPubliclyReachable, error.Message);
    }

    [Fact]
    public async Task refuses_a_name_that_does_not_resolve()
    {
        var guard = new FeedUrlGuard(new StubDnsResolver().Failing("nowhere.invalid"));

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => guard.PrepareFeedUrlAsync("https://nowhere.invalid/a.ics"));

        Assert.Equal(FeedUrlGuard.CouldNotResolve, error.Message);
    }

    [Fact]
    public async Task refuses_an_empty_resolution()
    {
        var guard = new FeedUrlGuard(new StubDnsResolver().Empty("void.example"));

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => guard.PrepareFeedUrlAsync("https://void.example/a.ics"));

        Assert.Equal(FeedUrlGuard.CouldNotResolve, error.Message);
    }

    [Fact]
    public async Task refuses_a_bare_loopback_literal()
    {
        // No DNS needed — the literal resolves to itself.
        var guard = new FeedUrlGuard(new StubDnsResolver().Resolving("127.0.0.1", "127.0.0.1"));

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => guard.PrepareFeedUrlAsync("http://127.0.0.1:9/private.ics"));

        Assert.Equal(FeedUrlGuard.NotPubliclyReachable, error.Message);
    }

    [Fact]
    public async Task looks_up_an_ipv6_literal_in_its_BRACKETED_form()
    {
        // Matches WHATWG `url.hostname`, which keeps the brackets.
        var resolver = new StubDnsResolver();
        var guard = new FeedUrlGuard(resolver);

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => guard.PrepareFeedUrlAsync("http://[::1]/a.ics"));

        Assert.Equal(FeedUrlGuard.CouldNotResolve, error.Message);
        Assert.Equal(new[] { "[::1]" }, resolver.Queried);
    }

    [Theory]
    [InlineData("[::1]")]
    [InlineData("[2606:4700:4700::1111]")]
    public async Task the_real_resolver_refuses_a_bracketed_literal_the_way_getaddrinfo_does(string host)
    {
        // Caught by a hand-rolled differential against the live reference, not by the
        // stub above: .NET's Dns.GetHostAddresses PARSES "[::1]" and succeeds, so
        // without this the guard would answer "not publicly reachable" where the
        // reference answers "could not be resolved". Both refuse — but the message is
        // API contract. Needs no network: it fails before any lookup.
        await Assert.ThrowsAsync<SocketException>(() => new SystemFeedDnsResolver().ResolveAsync(host));
    }

    [Fact]
    public async Task vets_the_metadata_endpoint_even_when_reached_through_webcal()
    {
        var guard = new FeedUrlGuard(new StubDnsResolver().Resolving("169.254.169.254", "169.254.169.254"));

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => guard.PrepareFeedUrlAsync("webcal://169.254.169.254/latest/meta-data"));

        Assert.Equal(FeedUrlGuard.NotPubliclyReachable, error.Message);
    }
}
