using System.Net;
using BlockRdpBruteForce.State;
using BlockRdpBruteForce.Tests.Fakes;
using BlockRdpBruteForce.Unblocking;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlockRdpBruteForce.Tests;

public class UnblockSchedulerTests : IDisposable
{
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UnblockSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "blockrdp-unblock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _gate.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private StateStore NewState() => new(Path.Combine(_dir, "state.json"));

    [Fact]
    public async Task RunOnceAsync_Removes_Expired_From_State_And_Firewall()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var expiring = IPAddress.Parse("203.0.113.1");
        var live = IPAddress.Parse("203.0.113.2");
        state.Upsert(expiring, t.AddMinutes(-60), TimeSpan.FromMinutes(5));
        state.Upsert(live, t, TimeSpan.FromMinutes(60));
        fw.SetIps(new[] { expiring, live });

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        var removed = await scheduler.RunOnceAsync(t);

        Assert.Equal(1, removed);
        Assert.Null(state.TryGet(expiring));
        Assert.NotNull(state.TryGet(live));
        Assert.DoesNotContain(expiring, fw.GetBlockedIps());
        Assert.Contains(live, fw.GetBlockedIps());
    }

    [Fact]
    public async Task RunOnceAsync_NoOp_When_Nothing_Expired()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        state.Upsert(IPAddress.Parse("203.0.113.1"), t, TimeSpan.FromMinutes(60));
        fw.AddIp(IPAddress.Parse("203.0.113.1"));

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        var removed = await scheduler.RunOnceAsync(t);

        Assert.Equal(0, removed);
        Assert.Equal(0, fw.SetCallCount);
        Assert.Single(fw.GetBlockedIps());
    }

    [Fact]
    public async Task RunOnceAsync_Persists_State_After_Removal()
    {
        var path = Path.Combine(_dir, "state.json");
        var state = new StateStore(path);
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        state.Upsert(IPAddress.Parse("203.0.113.1"), t.AddMinutes(-60), TimeSpan.FromMinutes(5));
        fw.AddIp(IPAddress.Parse("203.0.113.1"));

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        await scheduler.RunOnceAsync(t);

        var reloaded = new StateStore(path);
        reloaded.Load();
        Assert.Empty(reloaded.Snapshot());
    }

    [Fact]
    public async Task RunOnceAsync_Permanent_Bans_Are_Never_Expired()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        state.Upsert(IPAddress.Parse("203.0.113.1"), t.AddYears(-1), null);
        fw.AddIp(IPAddress.Parse("203.0.113.1"));

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        var removed = await scheduler.RunOnceAsync(t);

        Assert.Equal(0, removed);
        Assert.NotNull(state.TryGet(IPAddress.Parse("203.0.113.1")));
    }

    [Fact]
    public async Task RunOnceAsync_Acquires_And_Releases_The_Gate()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        state.Upsert(IPAddress.Parse("203.0.113.1"), DateTime.UtcNow.AddHours(-1), TimeSpan.FromMinutes(5));

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        await scheduler.RunOnceAsync(DateTime.UtcNow);

        Assert.True(await _gate.WaitAsync(TimeSpan.FromMilliseconds(50)));
        _gate.Release();
    }
}
