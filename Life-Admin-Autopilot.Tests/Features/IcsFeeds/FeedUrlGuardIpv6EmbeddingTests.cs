using Life_Admin_Autopilot.BLL.Features.IcsFeeds;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>
/// IPv4-in-IPv6 transition prefixes. Unwrapping only <c>::ffff:</c> left three other
/// formats that also embed an IPv4 address, so <c>64:ff9b::a9fe:a9fe</c> reached the
/// cloud metadata endpoint through a guard whose entire job is to stop exactly that.
///
/// <para>Every expectation here was proved against the reference implementation in
/// the same change — both servers were fixed together, so neither can drift.</para>
/// </summary>
public sealed class FeedUrlGuardIpv6EmbeddingTests
{
    [Theory]
    // 169.254.169.254 — the cloud metadata endpoint — in each disguise.
    [InlineData("::ffff:169.254.169.254", "IPv4-mapped, the only one handled before")]
    [InlineData("64:ff9b::a9fe:a9fe", "NAT64 well-known prefix, hex form")]
    [InlineData("64:ff9b::169.254.169.254", "NAT64, dotted form")]
    [InlineData("2002:a9fe:a9fe::", "6to4")]
    [InlineData("2001:0:0:0:0:0:5601:5601", "Teredo, IPv4 XOR'd with all-ones")]
    // and a private range through the same wrapper
    [InlineData("64:ff9b::0a00:0001", "NAT64 wrapping 10.0.0.1")]
    public void blocks_an_ipv4_smuggled_inside_ipv6(string address, string why)
    {
        Assert.True(FeedUrlGuard.IsPrivateIpv6(address), why);
    }

    [Theory]
    [InlineData("2606:4700:4700::1111", "Cloudflare DNS — a real public address")]
    [InlineData("2001:db8::1", "documentation range, carries no embedded v4")]
    [InlineData("2400:cb00::1", "public v6")]
    public void still_allows_genuinely_public_ipv6(string address, string why)
    {
        Assert.False(FeedUrlGuard.IsPrivateIpv6(address), why);
    }

    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void keeps_blocking_what_it_already_blocked(string address)
    {
        Assert.True(FeedUrlGuard.IsPrivateIpv6(address));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("::ffff:999.1.1.1")]
    public void unparseable_input_yields_no_embedded_address(string address)
    {
        // Must not throw. An address we cannot parse is not thereby safe — it simply
        // has no embedded v4 to check, and the prefix rules below still apply.
        _ = FeedUrlGuard.IsPrivateIpv6(address);
    }
}
