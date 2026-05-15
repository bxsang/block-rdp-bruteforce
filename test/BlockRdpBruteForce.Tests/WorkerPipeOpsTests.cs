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
            BlockDurationMinutes = new List<int> { 60 },
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

        var settingsPath = Path.Combine(_stateDir, "appsettings.json");
        var initialPayload = new ConfigPayload
        {
            FailureThreshold = options.Value.FailureThreshold,
            SlidingWindowMinutes = options.Value.SlidingWindowMinutes,
            BlockDurationMinutes = options.Value.BlockDurationMinutes.ToList(),
            Whitelist = options.Value.Whitelist.ToList(),
            FirewallScope = options.Value.FirewallScope,
            EvaluateNlaFallback = options.Value.EvaluateNlaFallback,
            HistoryRetentionDays = options.Value.HistoryRetentionDays,
            GeoLookupEnabled = options.Value.GeoLookupEnabled,
            IpInfoToken = options.Value.IpInfoToken,
            GeoRefreshIntervalDays = options.Value.GeoRefreshIntervalDays,
            AutoUpdateEnabled = options.Value.AutoUpdateEnabled,
            AutoUpdateCheckIntervalHours = options.Value.AutoUpdateCheckIntervalHours,
        };
        var settings = new SettingsWriter(initialPayload, settingsPath, NullLogger<SettingsWriter>.Instance);

        _worker = new Worker(
            options,
            new StaticOptionsMonitor<AppOptions>(options.Value),
            _state,
            _firewall,
            sync,
            unblock,
            gate,
            NullLoggerFactory.Instance,
            NullLogger<Worker>.Instance,
            settings);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
        Assert.Equal(new List<int> { 60 }, status.BlockDurationMinutes);
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
    public async Task UnblockAsync_clears_firewall_keeps_history_and_returns_was_blocked_true()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        _state.Upsert(ip, DateTime.UtcNow, TimeSpan.FromHours(1));
        _firewall.AddIp(ip);

        var result = await _worker.UnblockAsync(ip, CancellationToken.None);

        Assert.True(result.WasBlocked);
        Assert.Equal("1.2.3.4", result.Ip);
        var record = _state.TryGet(ip);
        Assert.NotNull(record);
        Assert.True(record!.BlockedUntilUtc <= DateTime.UtcNow);
        Assert.DoesNotContain(ip, _state.ActiveBlockedIps(DateTime.UtcNow));
        Assert.DoesNotContain(ip, _firewall.GetBlockedIps());
    }

    [Fact]
    public async Task UnblockAsync_for_already_historical_ip_returns_was_blocked_false()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        _state.Upsert(ip, DateTime.UtcNow.AddHours(-2), TimeSpan.FromHours(1));

        var result = await _worker.UnblockAsync(ip, CancellationToken.None);

        Assert.False(result.WasBlocked);
        Assert.NotNull(_state.TryGet(ip));
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

    [Fact]
    public void GetConfig_returns_current_settings()
    {
        var config = _worker.GetConfig();
        Assert.Equal(3, config.FailureThreshold);
        Assert.Equal(10, config.SlidingWindowMinutes);
        Assert.Equal(new List<int> { 60 }, config.BlockDurationMinutes);
        Assert.Equal(new List<string> { "10.0.0.0/8" }, config.Whitelist);
    }

    [Fact]
    public void SetConfig_whitelist_change_hot_applies_to_evaluator()
    {
        // Sanity: 192.168.1.5 currently not whitelisted
        Assert.Equal(1, _worker.GetStatus().WhitelistEntryCount);

        var result = _worker.SetConfig(
            new ConfigPayload { Whitelist = new List<string> { "10.0.0.0/8", "192.168.1.0/24" } },
            "test");

        Assert.False(result.RestartRequired);
        Assert.Contains("whitelist", result.AppliedHot);
        Assert.Equal(2, _worker.GetStatus().WhitelistEntryCount);
    }

    [Fact]
    public void SetConfig_threshold_change_requires_restart()
    {
        var result = _worker.SetConfig(
            new ConfigPayload { FailureThreshold = 7 },
            "test");

        Assert.True(result.RestartRequired);
        Assert.Empty(result.AppliedHot);
        // Status still reflects in-memory value (restart needed to pick up new threshold).
        Assert.Equal(3, _worker.GetStatus().FailureThreshold);
        // GetConfig returns the just-written file value.
        Assert.Equal(7, _worker.GetConfig().FailureThreshold);
    }

    [Fact]
    public void SetConfig_invalid_payload_throws_validation()
    {
        Assert.Throws<ConfigValidationException>(() => _worker.SetConfig(
            new ConfigPayload { FailureThreshold = 0 }, "test"));
    }
}
