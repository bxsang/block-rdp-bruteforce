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
    public async Task RunOnceAsync_Removes_Expired_From_Firewall_But_Keeps_State_As_History()
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
        var expiringRecord = state.TryGet(expiring);
        Assert.NotNull(expiringRecord);
        Assert.True(expiringRecord!.BlockedUntilUtc <= t);
        Assert.NotNull(state.TryGet(live));
        Assert.DoesNotContain(expiring, fw.GetBlockedIps());
        Assert.Contains(live, fw.GetBlockedIps());
    }

    [Fact]
    public async Task RunOnceAsync_Is_Idempotent_After_Cleanup()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var expiring = IPAddress.Parse("203.0.113.1");
        state.Upsert(expiring, t.AddMinutes(-60), TimeSpan.FromMinutes(5));
        fw.AddIp(expiring);

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        var first = await scheduler.RunOnceAsync(t);
        var second = await scheduler.RunOnceAsync(t);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
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
    public async Task RunOnceAsync_Keeps_History_Record_In_State()
    {
        var path = Path.Combine(_dir, "state.json");
        var state = new StateStore(path);
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var ip = IPAddress.Parse("203.0.113.1");
        state.Upsert(ip, t.AddMinutes(-60), TimeSpan.FromMinutes(5));
        fw.AddIp(ip);

        var scheduler = new UnblockScheduler(fw, state, _gate, NullLogger<UnblockScheduler>.Instance);
        await scheduler.RunOnceAsync(t);

        Assert.NotNull(state.TryGet(ip));
        Assert.DoesNotContain(ip, state.ActiveBlockedIps(t));
        Assert.DoesNotContain(ip, fw.GetBlockedIps());
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
    public async Task RunOnceAsync_Prunes_History_Older_Than_Retention_Window()
    {
        var path = Path.Combine(_dir, "state.json");
        var state = new StateStore(path);
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var oldHist = IPAddress.Parse("203.0.113.1");
        var newHist = IPAddress.Parse("203.0.113.2");
        var active = IPAddress.Parse("203.0.113.3");

        state.Upsert(oldHist, t.AddDays(-100), TimeSpan.FromMinutes(60));
        state.Upsert(newHist, t.AddDays(-30), TimeSpan.FromMinutes(60));
        state.Upsert(active, t, TimeSpan.FromHours(1));
        fw.AddIp(active);

        var scheduler = new UnblockScheduler(
            fw, state, _gate, NullLogger<UnblockScheduler>.Instance, historyRetentionDays: 90);
        await scheduler.RunOnceAsync(t);

        Assert.Null(state.TryGet(oldHist));
        Assert.NotNull(state.TryGet(newHist));
        Assert.NotNull(state.TryGet(active));

        var reloaded = new StateStore(path);
        reloaded.Load();
        Assert.Null(reloaded.TryGet(oldHist));
        Assert.NotNull(reloaded.TryGet(newHist));
    }

    [Fact]
    public async Task RunOnceAsync_Skips_Prune_When_Retention_Is_Zero()
    {
        var state = NewState();
        var fw = new InMemoryFirewallManager();
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var ancient = IPAddress.Parse("203.0.113.1");
        state.Upsert(ancient, t.AddYears(-5), TimeSpan.FromMinutes(5));

        var scheduler = new UnblockScheduler(
            fw, state, _gate, NullLogger<UnblockScheduler>.Instance, historyRetentionDays: 0);
        await scheduler.RunOnceAsync(t);

        Assert.NotNull(state.TryGet(ancient));
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
