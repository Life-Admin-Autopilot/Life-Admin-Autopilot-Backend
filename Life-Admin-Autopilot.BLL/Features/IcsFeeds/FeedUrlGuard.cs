using System.Globalization;
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
/// <b>IPv4-in-IPv6 transition prefixes — FIXED, on both servers together.</b>
/// <see cref="IsPrivateIpv6"/> used to unwrap only the <c>::ffff:</c> mapped form.
/// NAT64 (<c>64:ff9b::/96</c>), 6to4 (<c>2002::/16</c>) and Teredo
/// (<c>2001::/32</c>) also embed an IPv4 address in their low bits, so
/// <c>64:ff9b::a9fe:a9fe</c> was the metadata endpoint wearing a v6 costume and the
/// guard waved it through. It bites only where the egress path actually does
/// NAT64/DNS64 or has a 6to4 relay — which describes IPv6-only container egress on
/// more than one cloud.
///
/// <para>The identical hole existed in the reference and was closed in the same
/// change, so the two servers still refuse the same URLs with the same message
/// during cut-over. See <see cref="EmbeddedIpv4"/>. Fixing one side alone would have
/// been a divergence the parity harness reports for a good reason, which is the
/// worst kind of red row.</para>
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

        // Every transition format that carries an IPv4 has to be unwrapped, or it
        // bypasses the v4 rules entirely. `::ffff:` alone is not enough — see
        // EmbeddedIpv4. Fixed on BOTH servers in one change, so the two still refuse
        // the same URLs with the same message.
        foreach (var embedded in EmbeddedIpv4(lower))
        {
            if (IsPrivateIpv4(embedded))
            {
                return true;
            }
        }

        var head = lower.Split(':').FirstOrDefault() ?? string.Empty;
        if (UniqueLocalV6.IsMatch(head)) return true; // unique local, fc00::/7
        if (LinkLocalV6.IsMatch(head)) return true;   // link-local, fe80::/10

        return false;
    }

    /// <summary>
    /// Expand any IPv6 form — <c>::</c> compression, a trailing dotted quad — into
    /// its eight 16-bit groups. Null for anything unparseable, which callers treat
    /// as "no embedded v4" rather than as safe.
    /// </summary>
    private static ushort[]? ExpandIpv6(string ip)
    {
        var s = ip.Split('%')[0];

        var dotted = TrailingDottedQuad.Match(s);
        if (dotted.Success)
        {
            var o = dotted.Groups[1].Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 256).ToArray();
            if (o.Any(n => n > 255))
            {
                return null;
            }

            s = string.Concat(
                s.AsSpan(0, dotted.Groups[1].Index),
                ((o[0] << 8) | o[1]).ToString("x", CultureInfo.InvariantCulture),
                ":",
                ((o[2] << 8) | o[3]).ToString("x", CultureInfo.InvariantCulture));
        }

        var halves = s.Split("::");
        if (halves.Length > 2)
        {
            return null;
        }

        var head = halves[0].Length > 0 ? halves[0].Split(':') : [];
        var tail = halves.Length == 2 && halves[1].Length > 0 ? halves[1].Split(':') : [];
        if (halves.Length == 1 && head.Length != 8)
        {
            return null;
        }

        var fill = 8 - head.Length - tail.Length;
        if (fill < 0)
        {
            return null;
        }

        var groups = head
            .Concat(halves.Length == 2 ? Enumerable.Repeat("0", fill) : [])
            .Concat(tail)
            .ToArray();

        if (groups.Length != 8)
        {
            return null;
        }

        var outGroups = new ushort[8];
        for (var i = 0; i < 8; i++)
        {
            if (groups[i].Length == 0)
            {
                outGroups[i] = 0;
                continue;
            }

            if (!ushort.TryParse(groups[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out outGroups[i]))
            {
                return null;
            }
        }

        return outGroups;
    }

    /// <summary>
    /// Every IPv4 address an IPv6 address can carry.
    ///
    /// <para>Unwrapping only <c>::ffff:</c> leaves three other transition formats that
    /// also embed IPv4, so <c>64:ff9b::a9fe:a9fe</c> is 169.254.169.254 — the cloud
    /// metadata endpoint — in an IPv6 costume, and the guard waved it through. Each
    /// address found here is put through the v4 rules.</para>
    /// </summary>
    private static IEnumerable<string> EmbeddedIpv4(string ip)
    {
        var g = ExpandIpv6(ip);
        if (g is null)
        {
            yield break;
        }

        static string V4(ushort hi, ushort lo) =>
            $"{hi >> 8}.{hi & 0xff}.{lo >> 8}.{lo & 0xff}";

        // ::ffff:a.b.c.d (mapped) and ::a.b.c.d (compatible - deprecated but routable)
        if (g[0] == 0 && g[1] == 0 && g[2] == 0 && g[3] == 0 && g[4] == 0 && (g[5] == 0xffff || g[5] == 0))
        {
            yield return V4(g[6], g[7]);
        }

        // NAT64 well-known prefix, RFC 6052
        if (g[0] == 0x0064 && g[1] == 0xff9b && g[2] == 0 && g[3] == 0 && g[4] == 0 && g[5] == 0)
        {
            yield return V4(g[6], g[7]);
        }

        // 6to4, RFC 3056 - the IPv4 sits in groups 1..2
        if (g[0] == 0x2002)
        {
            yield return V4(g[1], g[2]);
        }

        // Teredo, RFC 4380 - client IPv4 is the last 32 bits, XOR'd with all-ones
        if (g[0] == 0x2001 && g[1] == 0x0000)
        {
            yield return V4((ushort)~g[6], (ushort)~g[7]);
        }
    }

    private static readonly Regex TrailingDottedQuad =
        new(@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})$", RegexOptions.Compiled);

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
