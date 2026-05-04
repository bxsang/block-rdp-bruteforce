using System.Net;
using System.Net.Sockets;
using BlockRdpBruteForce.Firewall;
using BlockRdpBruteForce.Tests.Fakes;

namespace BlockRdpBruteForce.Tests;

public class FirewallManagerFakeTests
{
    [Fact]
    public void AddIp_Stores_The_Ip()
    {
        var fw = new InMemoryFirewallManager();
        fw.AddIp(IPAddress.Parse("203.0.113.1"));
        Assert.Contains(IPAddress.Parse("203.0.113.1"), fw.GetBlockedIps());
        Assert.Equal(1, fw.AddCallCount);
    }

    [Fact]
    public void AddIp_Is_Idempotent()
    {
        var fw = new InMemoryFirewallManager();
        var ip = IPAddress.Parse("203.0.113.1");
        fw.AddIp(ip);
        fw.AddIp(ip);
        Assert.Single(fw.GetBlockedIps());
    }

    [Fact]
    public void RemoveIp_Drops_The_Ip()
    {
        var fw = new InMemoryFirewallManager();
        var ip = IPAddress.Parse("203.0.113.1");
        fw.AddIp(ip);
        fw.RemoveIp(ip);
        Assert.Empty(fw.GetBlockedIps());
        Assert.Equal(1, fw.RemoveCallCount);
    }

    [Fact]
    public void RemoveIp_Unknown_Is_NoOp()
    {
        var fw = new InMemoryFirewallManager();
        fw.RemoveIp(IPAddress.Parse("203.0.113.99"));
        Assert.Empty(fw.GetBlockedIps());
    }

    [Fact]
    public void SetIps_Replaces_All()
    {
        var fw = new InMemoryFirewallManager();
        fw.AddIp(IPAddress.Parse("203.0.113.1"));
        fw.SetIps(new[] { IPAddress.Parse("198.51.100.5"), IPAddress.Parse("198.51.100.6") });
        var blocked = fw.GetBlockedIps();
        Assert.Equal(2, blocked.Count);
        Assert.DoesNotContain(IPAddress.Parse("203.0.113.1"), blocked);
        Assert.Contains(IPAddress.Parse("198.51.100.5"), blocked);
    }

    [Fact]
    public void Chunker_Splits_Past_MaxPerRule()
    {
        var ips = Enumerable.Range(0, 2500)
            .Select(i => IPAddress.Parse($"10.{(i >> 16) & 0xFF}.{(i >> 8) & 0xFF}.{i & 0xFF}"))
            .ToArray();

        var chunks = FirewallRuleChunker.Chunk(ips, "BlockRDPBruteForce", maxPerRule: 1000);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("BlockRDPBruteForce-v4", chunks[0].RuleName);
        Assert.Equal("BlockRDPBruteForce-v4-2", chunks[1].RuleName);
        Assert.Equal("BlockRDPBruteForce-v4-3", chunks[2].RuleName);
        Assert.Equal(1000, chunks[0].Addresses.Count);
        Assert.Equal(1000, chunks[1].Addresses.Count);
        Assert.Equal(500, chunks[2].Addresses.Count);
        Assert.All(chunks, c => Assert.Equal(AddressFamily.InterNetwork, c.Family));
    }

    [Fact]
    public void Chunker_Separates_V4_And_V6()
    {
        var ips = new[]
        {
            IPAddress.Parse("203.0.113.1"),
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("203.0.113.2"),
            IPAddress.Parse("2001:db8::2"),
        };

        var chunks = FirewallRuleChunker.Chunk(ips, "BlockRDPBruteForce", maxPerRule: 100);

        var v4 = chunks.Single(c => c.Family == AddressFamily.InterNetwork);
        var v6 = chunks.Single(c => c.Family == AddressFamily.InterNetworkV6);
        Assert.Equal("BlockRDPBruteForce-v4", v4.RuleName);
        Assert.Equal("BlockRDPBruteForce-v6", v6.RuleName);
        Assert.Equal(2, v4.Addresses.Count);
        Assert.Equal(2, v6.Addresses.Count);
    }

    [Fact]
    public void Chunker_Skips_Empty_Family()
    {
        var ips = new[] { IPAddress.Parse("203.0.113.1") };
        var chunks = FirewallRuleChunker.Chunk(ips, "BlockRDPBruteForce", maxPerRule: 100);
        Assert.Single(chunks);
        Assert.Equal(AddressFamily.InterNetwork, chunks[0].Family);
    }

    [Fact]
    public void Chunker_Deduplicates()
    {
        var ip = IPAddress.Parse("203.0.113.1");
        var chunks = FirewallRuleChunker.Chunk(new[] { ip, ip, ip }, "BlockRDPBruteForce", 100);
        Assert.Single(chunks);
        Assert.Single(chunks[0].Addresses);
    }

    [Fact]
    public void Chunker_Sorts_Deterministically()
    {
        var unsorted = new[]
        {
            IPAddress.Parse("203.0.113.5"),
            IPAddress.Parse("203.0.113.1"),
            IPAddress.Parse("203.0.113.3"),
        };
        var chunks = FirewallRuleChunker.Chunk(unsorted, "BlockRDPBruteForce", 100);
        Assert.Equal(
            new[] { "203.0.113.1", "203.0.113.3", "203.0.113.5" },
            chunks[0].Addresses.Select(a => a.ToString()));
    }

    [Fact]
    public void Chunker_Rejects_InvalidArgs()
    {
        Assert.Throws<ArgumentException>(() => FirewallRuleChunker.Chunk(Array.Empty<IPAddress>(), "", 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => FirewallRuleChunker.Chunk(Array.Empty<IPAddress>(), "x", 0));
    }

    [Fact]
    public void Format_RemoteAddresses_Joins_With_Comma()
    {
        var csv = FirewallRuleChunker.FormatRemoteAddresses(new[]
        {
            IPAddress.Parse("203.0.113.1"),
            IPAddress.Parse("203.0.113.2"),
        });
        Assert.Equal("203.0.113.1,203.0.113.2", csv);
    }
}
