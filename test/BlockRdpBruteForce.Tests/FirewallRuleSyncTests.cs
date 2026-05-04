using System.Net;
using BlockRdpBruteForce.Firewall;
using BlockRdpBruteForce.State;
using BlockRdpBruteForce.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlockRdpBruteForce.Tests;

public class FirewallRuleSyncTests : IDisposable
{
    private readonly string _dir;

    public FirewallRuleSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "blockrdp-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private StateStore NewState() => new(Path.Combine(_dir, "state.json"));

    private static FirewallRuleSync NewSync(IFirewallManager fw, StateStore state) =>
        new(fw, state, NullLogger<FirewallRuleSync>.Instance);

    [Fact]
    public void Sync_NoOp_When_Already_In_Sync()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = DateTime.UtcNow;

        var ip = IPAddress.Parse("203.0.113.1");
        state.Upsert(ip, t, TimeSpan.FromMinutes(60));
        fw.AddIp(ip);

        var result = NewSync(fw, state).Sync(t);

        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Extra);
        Assert.Equal(1, result.Total);
        Assert.Equal(0, fw.SetCallCount);
    }

    [Fact]
    public void Sync_Adds_Missing_Ips_From_State()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = DateTime.UtcNow;

        state.Upsert(IPAddress.Parse("203.0.113.1"), t, TimeSpan.FromMinutes(60));
        state.Upsert(IPAddress.Parse("203.0.113.2"), t, null);

        var result = NewSync(fw, state).Sync(t);

        Assert.Equal(2, result.Missing);
        Assert.Equal(0, result.Extra);
        Assert.Equal(2, fw.GetBlockedIps().Count);
    }

    [Fact]
    public void Sync_Removes_Stale_Ips_From_Firewall()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = DateTime.UtcNow;

        fw.AddIp(IPAddress.Parse("198.51.100.99"));

        var result = NewSync(fw, state).Sync(t);

        Assert.Equal(0, result.Missing);
        Assert.Equal(1, result.Extra);
        Assert.Empty(fw.GetBlockedIps());
    }

    [Fact]
    public void Sync_Excludes_Expired_State_Records()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        state.Upsert(IPAddress.Parse("203.0.113.1"), t.AddMinutes(-60), TimeSpan.FromMinutes(5));
        state.Upsert(IPAddress.Parse("203.0.113.2"), t, TimeSpan.FromMinutes(60));

        var result = NewSync(fw, state).Sync(t);

        Assert.Equal(1, result.Total);
        Assert.Single(fw.GetBlockedIps());
        Assert.Contains(IPAddress.Parse("203.0.113.2"), fw.GetBlockedIps());
    }
}
