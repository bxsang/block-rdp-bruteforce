using System.Net;
using BlockRdpBruteForce.Detection;

namespace BlockRdpBruteForce.Tests;

public class WhitelistEvaluatorTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.42", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("192.168.1.128/25", "192.168.1.200", true)]
    [InlineData("192.168.1.128/25", "192.168.1.127", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", false)]
    [InlineData("203.0.113.5/32", "203.0.113.5", true)]
    [InlineData("203.0.113.5/32", "203.0.113.6", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void Matches_IPv4_Cases(string entry, string probe, bool expected)
    {
        var eval = new WhitelistEvaluator(new[] { entry });
        Assert.Equal(expected, eval.IsWhitelisted(IPAddress.Parse(probe)));
    }

    [Theory]
    [InlineData("::1", "::1", true)]
    [InlineData("::1", "::2", false)]
    [InlineData("2001:db8::/32", "2001:db8:abcd::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8:1234::/48", "2001:db8:1234:5678::1", true)]
    [InlineData("2001:db8:1234::/48", "2001:db8:1235::1", false)]
    [InlineData("2001:db8::dead:beef/128", "2001:db8::dead:beef", true)]
    [InlineData("2001:db8::dead:beef/128", "2001:db8::dead:bef0", false)]
    [InlineData("::/0", "2001:db8::1", true)]
    public void Matches_IPv6_Cases(string entry, string probe, bool expected)
    {
        var eval = new WhitelistEvaluator(new[] { entry });
        Assert.Equal(expected, eval.IsWhitelisted(IPAddress.Parse(probe)));
    }

    [Fact]
    public void Family_Mismatch_Does_Not_Match()
    {
        var eval = new WhitelistEvaluator(new[] { "10.0.0.0/8" });
        Assert.False(eval.IsWhitelisted(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void Multiple_Entries_Any_Match_Wins()
    {
        var eval = new WhitelistEvaluator(new[] { "10.0.0.0/8", "::1", "192.168.0.0/16" });
        Assert.True(eval.IsWhitelisted(IPAddress.Parse("192.168.5.5")));
        Assert.True(eval.IsWhitelisted(IPAddress.Parse("::1")));
        Assert.False(eval.IsWhitelisted(IPAddress.Parse("172.16.0.1")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/")]
    [InlineData("/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_Entries_Are_Silently_Dropped(string entry)
    {
        var eval = new WhitelistEvaluator(new[] { entry });
        Assert.Equal(0, eval.EntryCount);
        Assert.False(eval.IsWhitelisted(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void TryParse_Canonicalizes_Network_Bits()
    {
        Assert.True(WhitelistEvaluator.TryParse("10.1.2.3/8", out var network, out var prefix, out _));
        Assert.Equal(8, prefix);
        Assert.Equal(new byte[] { 10, 0, 0, 0 }, network);
    }

    [Fact]
    public void TryParse_Canonicalizes_NonByteAligned_Prefix()
    {
        Assert.True(WhitelistEvaluator.TryParse("192.168.1.250/28", out var network, out _, out _));
        Assert.Equal(new byte[] { 192, 168, 1, 240 }, network);
    }

    [Fact]
    public void Empty_Whitelist_Matches_Nothing()
    {
        var eval = new WhitelistEvaluator(Array.Empty<string>());
        Assert.False(eval.IsWhitelisted(IPAddress.Parse("8.8.8.8")));
        Assert.False(eval.IsWhitelisted(IPAddress.Parse("::1")));
    }
}
