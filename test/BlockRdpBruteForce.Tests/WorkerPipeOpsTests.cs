using System.Net;
using System.Runtime.Versioning;
using BlockRdpBruteForce;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Firewall;
using BlockRdpBruteForce.Ipc;
using BlockRdpBruteForce.State;
using BlockRdpBruteForce.Tests.Fakes;
using BlockRdpBruteForce.Unblocking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class WorkerPipeOpsTests : IDisposable
{
    private readonly string _stateDir;
    private readonly string _statePath;
    private readonly InMemoryFirewallManager _firewall;
    private readonly StateStore _state;
    private readonly Worker _worker;

    public WorkerPipeOpsTests()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"brbf-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stateDir);
        _statePath = Path.Combine(_stateDir, "state.json");

        var options = Options.Create(new AppOptions
        {
            FailureThreshold = 3,
            SlidingWindowMinutes = 10,
            BlockDurationMinutes = 60,
            FirewallRuleName = "TestRule",
            Whitelist = new() { "10.0.0.0/8" },
            StateFilePath = _statePath,
            EvaluateNlaFallback = true,
        });

        _firewall = new InMemoryFirewallManager();
        _state = new StateStore(_statePath);
        var gate = new SemaphoreSlim(1, 1);
        var sync = new FirewallRuleSync(_firewall, _state, NullLogger<FirewallRuleSync>.Instance);
        var unblock = new UnblockScheduler(_firewall, _state, gate, NullLogger<UnblockScheduler>.Instance);

        _worker = new Worker(
            options,
            _state,
            _firewall,
            sync,
            unblock,
            gate,
            NullLoggerFactory.Instance,
            NullLogger<Worker>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void GetStatus_returns_options_and_started_time()
    {
        var status = _worker.GetStatus();

        Assert.Equal("BlockRdpBruteForce", status.ServiceName);
        Assert.Equal(3, status.FailureThreshold);
        Assert.Equal(10, status.SlidingWindowMinutes);
        Assert.Equal(60, status.BlockDurationMinutes);
        Assert.Equal("TestRule", status.FirewallRuleName);
        Assert.Equal(1, status.WhitelistEntryCount);
        Assert.Equal(0, status.BlockedIpCount);
        Assert.True(status.EvaluateNlaFallback);
        Assert.Null(status.PausedUntilUtc);
        Assert.True(status.StartedUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void GetStatus_reflects_blocked_ip_count()
    {
        _state.Upsert(IPAddress.Parse("1.2.3.4"), DateTime.UtcNow, TimeSpan.FromHours(1));
        _state.Upsert(IPAddress.Parse("5.6.7.8"), DateTime.UtcNow, TimeSpan.FromHours(1));

        var status = _worker.GetStatus();

        Assert.Equal(2, status.BlockedIpCount);
    }

    [Fact]
    public void GetList_returns_all_state_records()
    {
        var now = DateTime.UtcNow;
        _state.Upsert(IPAddress.Parse("1.2.3.4"), now, TimeSpan.FromHours(1));
        _state.Upsert(IPAddress.Parse("9.9.9.9"), now, blockDuration: null);

        var list = _worker.GetList();

        Assert.Equal(2, list.Count);
        var permanent = list.Single(e => e.Ip == "9.9.9.9");
        Assert.Null(permanent.BlockedUntilUtc);
    }

    [Fact]
    public async Task UnblockAsync_removes_state_firewall_and_returns_was_blocked_true()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        _state.Upsert(ip, DateTime.UtcNow, TimeSpan.FromHours(1));
        _firewall.AddIp(ip);

        var result = await _worker.UnblockAsync(ip, CancellationToken.None);

        Assert.True(result.WasBlocked);
        Assert.Equal("1.2.3.4", result.Ip);
        Assert.Null(_state.TryGet(ip));
        Assert.DoesNotContain(ip, _firewall.GetBlockedIps());
    }

    [Fact]
    public async Task UnblockAsync_for_unknown_ip_returns_was_blocked_false()
    {
        var result = await _worker.UnblockAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None);

        Assert.False(result.WasBlocked);
        Assert.Equal("8.8.8.8", result.Ip);
    }

    [Fact]
    public async Task UnblockAsync_persists_remaining_blocks_to_firewall()
    {
        var ip1 = IPAddress.Parse("1.2.3.4");
        var ip2 = IPAddress.Parse("5.6.7.8");
        _state.Upsert(ip1, DateTime.UtcNow, TimeSpan.FromHours(1));
        _state.Upsert(ip2, DateTime.UtcNow, TimeSpan.FromHours(1));
        _firewall.AddIp(ip1);
        _firewall.AddIp(ip2);

        await _worker.UnblockAsync(ip1, CancellationToken.None);

        var remaining = _firewall.GetBlockedIps();
        Assert.Single(remaining);
        Assert.Contains(ip2, remaining);
    }

    [Fact]
    public void Pause_sets_paused_until_in_future()
    {
        var before = DateTime.UtcNow;
        var payload = _worker.Pause(TimeSpan.FromMinutes(60));

        Assert.NotNull(payload.PausedUntilUtc);
        Assert.True(payload.PausedUntilUtc!.Value >= before.AddMinutes(59));
        Assert.Equal(payload.PausedUntilUtc, _worker.GetStatus().PausedUntilUtc);
    }

    [Fact]
    public void Resume_clears_paused_state()
    {
        _worker.Pause(TimeSpan.FromMinutes(60));
        var resumed = _worker.Resume();

        Assert.Null(resumed.PausedUntilUtc);
        Assert.Null(_worker.GetStatus().PausedUntilUtc);
    }

    [Fact]
    public void Pause_with_zero_duration_resumes()
    {
        _worker.Pause(TimeSpan.FromMinutes(60));
        var payload = _worker.Pause(TimeSpan.Zero);

        Assert.Null(payload.PausedUntilUtc);
    }
}
