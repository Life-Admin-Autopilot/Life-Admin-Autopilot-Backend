using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <summary>
/// Raised by every refusal in <see cref="FeedUrlGuard"/>. The MESSAGE is part of
/// the API contract — the subscribe route re-throws it verbatim as the
/// <c>unsafe_feed_url</c> message, and there are exactly four distinct strings.
/// Do not reword them.
/// </summary>
public sealed class UnsafeFeedUrlException : Exception
{
    public UnsafeFeedUrlException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// Resolves a hostname to its addresses. A seam purely so the guard can be unit
/// tested without a network — the SSRF rules are the highest-risk code in this
/// slice and the parity harness cannot reach them.
/// </summary>
public interface IFeedDnsResolver
{
    /// <summary>
    /// Mirrors Node's <c>dns.lookup(host, { all: true })</c>: every A and AAAA
    /// record, in resolver order. Throws when the name does not resolve.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SystemFeedDnsResolver : IFeedDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        // A BRACKETED IPv6 literal is not a resolvable name.
        //
        // WHATWG `url.hostname` keeps the brackets, and `getaddrinfo` refuses that
        // form — so the reference answers "That address could not be resolved." for
        // `http://[::1]/`. .NET is more forgiving: `Dns.GetHostAddressesAsync("[::1]")`
        // parses the literal and succeeds, which would surface the guard's OTHER
        // message ("not publicly reachable") for the same input. Both refuse the
        // request; only the message differs, and the message is API contract.
        // Measured against the live reference.
        if (host.StartsWith('['))
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Port of <c>server/src/modules/integrations/ics/feedUrl.ts</c> — normalise and
/// vet a user-supplied calendar feed URL before the server fetches it.
///
/// <para>
/// This is a server-side request to an address the USER chose, which is the
/// textbook SSRF setup: without a guard, "subscribe to my school calendar" becomes
/// a way to make the backend GET <c>http://169.254.169.254/</c> (cloud instance
/// metadata, i.e. credentials), or reach anything else inside the deployment's
/// network that is not exposed publicly.
/// </para>
///
/// <para>
/// The guard is deliberately conservative. A feed wrongly refused is a support
/// ticket; a feed wrongly fetched can exfiltrate infrastructure credentials.
/// </para>
///
/// <para>
/// <b>Second undocumented gap, shared with the reference: IPv4-in-IPv6 transition
/// prefixes.</b> <see cref="IsPrivateIpv6"/> unwraps only the <c>::ffff:</c> mapped
/// form. NAT64 (<c>64:ff9b::/96</c>), 6to4 (<c>2002::/16</c>) and Teredo
/// (<c>2001::/32</c>) also embed an IPv4 address in their low bits, so
/// <c>64:ff9b::a9fe:a9fe</c> is the metadata endpoint wearing a v6 costume and this
/// guard passes it. It matters only where the egress path actually does NAT64/DNS64
/// or has a 6to4 relay — but that describes IPv6-only container egress on more than
/// one cloud. Deliberately NOT fixed here: the reference has the identical hole, and
/// the two servers have to refuse the same URLs with the same message during
/// cut-over. Raised for a decision that lands on BOTH servers at once, rather than
/// quietly diverging.
/// </para>
///
/// <para>
/// <b>What this does NOT solve, stated rather than hidden: DNS rebinding.</b> A
/// hostname can resolve to a public address here and a private one microseconds
/// later when the socket actually opens. Closing that requires pinning the
/// connection to the vetted IP, which neither Node's global <c>fetch</c> nor this
/// port does. The mitigations are the same as the reference's: re-vet EVERY
/// redirect hop (see <see cref="FeedFetcher"/>) and run the fetcher without
/// ambient network credentials. This is a known, accepted gap — not an oversight,
/// and not something to close unilaterally in a parity port.
/// </para>
/// </summary>
public sealed class FeedUrlGuard
{
    /// <summary>The four refusal messages, verbatim from the reference.</summary>
    public const string NotAValidUrl = "That is not a valid URL.";

    public const string MustUseHttps = "Calendar feeds must use https.";
    public const string NoCredentials = "Calendar feed URLs must not contain credentials.";
    public const string CouldNotResolve = "That address could not be resolved.";
    public const string NotPubliclyReachable = "That address is not publicly reachable.";

    private static readonly Regex WebcalPrefix = new(@"^webcal://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary><c>::ffff:10.0.0.1</c> — must be unwrapped or it bypasses every v4 rule.</summary>
    private static readonly Regex Ipv4Mapped =
        new(@"^::ffff:(\d+\.\d+\.\d+\.\d+)$", RegexOptions.Compiled);

    private static readonly Regex UniqueLocalV6 = new("^f[cd]", RegexOptions.Compiled);
    private static readonly Regex LinkLocalV6 = new("^fe[89ab]", RegexOptions.Compiled);

    private readonly IFeedDnsResolver _dns;

    public FeedUrlGuard(IFeedDnsResolver dns)
    {
        _dns = dns;
    }

    /// <summary>
    /// <c>webcal:</c> is not a real scheme — it is <c>https:</c> wearing a hat so
    /// that clicking a link opens a calendar app. Every publisher emits it; every
    /// consumer rewrites it.
    ///
    /// <para>
    /// The swap happens on the STRING, before parsing. <c>webcal:</c> is a
    /// non-special scheme per the WHATWG URL spec, so assigning the scheme after
    /// parsing is silently ignored and the URL would stay <c>webcal:</c> — then fail
    /// the scheme check below with a confusing message.
    /// </para>
    /// </summary>
    public static Uri NormalizeFeedUrl(string input)
    {
        var trimmed = (input ?? string.Empty).Trim();

        var rewritten = WebcalPrefix.IsMatch(trimmed)
            ? $"https://{trimmed["webcal://".Length..]}"
            : trimmed;

        if (!Uri.TryCreate(rewritten, UriKind.Absolute, out var parsed))
        {
            throw new UnsafeFeedUrlException(NotAValidUrl);
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            throw new UnsafeFeedUrlException(MustUseHttps);
        }

        // ORDER: the scheme check comes FIRST because a hostless non-special scheme
        // must reach the https message, not this one. `file:///etc/passwd` and
        // `data:text/calendar,...` both parse with an empty host in .NET, whereas the
        // reference's WHATWG parser accepts them as opaque URLs and then rejects the
        // scheme — so testing emptiness first would answer "That is not a valid URL."
        // where the reference answers "Calendar feeds must use https." Both verified
        // live. What remains here is `https://`, which the reference's URL
        // constructor rejects outright because a SPECIAL scheme requires a host.
        if (string.IsNullOrEmpty(parsed.Host))
        {
            throw new UnsafeFeedUrlException(NotAValidUrl);
        }

        // Credentials in the URL are a redirect-laundering trick and never
        // legitimate on a public feed.
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new UnsafeFeedUrlException(NoCredentials);
        }

        return parsed;
    }

    /// <summary>
    /// Port of the reference's <c>ipv4IsPrivate</c>.
    ///
    /// <para><b>Fails closed:</b> anything that is not four parseable octets is
    /// treated as private, because "I could not read this address" must never mean
    /// "so fetch it".</para>
    /// </summary>
    public static bool IsPrivateIpv4(string ip)
    {
        var parts = (ip ?? string.Empty).Split('.');
        if (parts.Length != 4)
        {
            return true;
        }

        var octets = new int[4];
        for (var i = 0; i < 4; i += 1)
        {
            if (!int.TryParse(parts[i], out octets[i]))
            {
                return true;
            }
        }

        var a = octets[0];
        var b = octets[1];

        if (a == 0) return true;                        // "this network"
        if (a == 10) return true;                       // RFC1918
        if (a == 127) return true;                      // loopback
        if (a == 169 && b == 254) return true;          // link-local, incl. cloud metadata
        if (a == 172 && b >= 16 && b <= 31) return true; // RFC1918
        if (a == 192 && b == 168) return true;          // RFC1918
        if (a == 192 && b == 0) return true;            // IETF protocol assignments
        if (a == 100 && b >= 64 && b <= 127) return true; // CGNAT
        if (a == 198 && (b == 18 || b == 19)) return true; // benchmarking
        if (a >= 224) return true;                      // multicast + reserved

        return false;
    }

    /// <summary>Port of the reference's <c>ipv6IsPrivate</c>.</summary>
    public static bool IsPrivateIpv6(string ip)
    {
        var lower = (ip ?? string.Empty).ToLowerInvariant();

        if (lower is "::" or "::1")
        {
            return true; // unspecified, loopback
        }

        var mapped = Ipv4Mapped.Match(lower);
        if (mapped.Success)
        {
            return IsPrivateIpv4(mapped.Groups[1].Value);
        }

        var head = lower.Split(':').FirstOrDefault() ?? string.Empty;
        if (UniqueLocalV6.IsMatch(head)) return true; // unique local, fc00::/7
        if (LinkLocalV6.IsMatch(head)) return true;   // link-local, fe80::/10

        return false;
    }

    /// <summary>
    /// Port of the exported <c>isPrivateAddress(ip, family)</c>. <paramref name="family"/>
    /// is 4 or 6, matching Node's <c>dns.lookup</c> result.
    /// </summary>
    public static bool IsPrivateAddress(string ip, int family) =>
        family == 6 ? IsPrivateIpv6(ip) : IsPrivateIpv4(ip);

    /// <summary>
    /// The same rule applied to a resolved <see cref="IPAddress"/>. An IPv4-mapped
    /// v6 address keeps its v6 family here and is unwrapped by
    /// <see cref="IsPrivateIpv6"/>, exactly as the string form is.
    /// </summary>
    public static bool IsPrivateAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? IsPrivateIpv6(address.ToString())
            : IsPrivateIpv4(address.ToString());

    /// <summary>
    /// Resolve the hostname and refuse anything that lands inside the deployment's
    /// own network. Returns the vetted URL so callers cannot forget to use the
    /// normalised form.
    /// </summary>
    public async Task<Uri> AssertPublicFeedUrlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IPAddress> resolved;
        try
        {
            // `Uri.Host` keeps the brackets on an IPv6 literal, matching WHATWG
            // `url.hostname`. That is why `http://[::1]/` reports "could not be
            // resolved" rather than "not publicly reachable" on both servers: the
            // bracketed form is not a resolvable name. Verified live.
            resolved = await _dns.ResolveAsync(url.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw new UnsafeFeedUrlException(CouldNotResolve);
        }

        if (resolved.Count == 0)
        {
            throw new UnsafeFeedUrlException(CouldNotResolve);
        }

        // ALL results must be public. A hostname with one public and one private A
        // record is an attack, not a misconfiguration.
        foreach (var address in resolved)
        {
            if (IsPrivateAddress(address))
            {
                throw new UnsafeFeedUrlException(NotPubliclyReachable);
            }
        }

        return url;
    }

    /// <summary>Normalise and vet in one step. The only entry point callers should use.</summary>
    public Task<Uri> PrepareFeedUrlAsync(string input, CancellationToken cancellationToken = default) =>
        AssertPublicFeedUrlAsync(NormalizeFeedUrl(input), cancellationToken);
}
