using System.Net;
using System.Runtime.Versioning;
using System.Threading.Channels;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Detection;
using BlockRdpBruteForce.Eventing;
using BlockRdpBruteForce.Firewall;
using BlockRdpBruteForce.Ipc;
using BlockRdpBruteForce.State;
using BlockRdpBruteForce.Unblocking;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce;

[SupportedOSPlatform("windows")]
public sealed class Worker : BackgroundService, IPipeOps
{
    private static readonly TimeSpan UnblockInterval = TimeSpan.FromMinutes(1);

    private readonly AppOptions _options;
    private readonly StateStore _state;
    private readonly IFirewallManager _firewall;
    private readonly FirewallRuleSync _sync;
    private readonly UnblockScheduler _unblock;
    private readonly SemaphoreSlim _gate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Worker> _log;
    private readonly FailureTracker _tracker;
    private WhitelistEvaluator _whitelist;
    private readonly SettingsWriter? _settings;
    private readonly Channel<FailedLogon> _channel;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private long _pauseUntilUtcTicks;

    public Worker(
        IOptions<AppOptions> options,
        StateStore state,
        IFirewallManager firewall,
        FirewallRuleSync sync,
        UnblockScheduler unblock,
        SemaphoreSlim gate,
        ILoggerFactory loggerFactory,
        ILogger<Worker> log,
        SettingsWriter? settings = null)
    {
        _options = options.Value;
        _state = state;
        _firewall = firewall;
        _sync = sync;
        _unblock = unblock;
        _gate = gate;
        _loggerFactory = loggerFactory;
        _log = log;
        _settings = settings;

        _tracker = new FailureTracker(
            _options.FailureThreshold,
            TimeSpan.FromMinutes(_options.SlidingWindowMinutes));
        _whitelist = new WhitelistEvaluator(_options.Whitelist);

        _channel = Channel.CreateBounded<FailedLogon>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "BlockRdpBruteForce starting (threshold={Threshold} in {Window}m, blockDuration={BlockMin}m, whitelist={WhitelistCount})",
            _options.FailureThreshold,
            _options.SlidingWindowMinutes,
            _options.BlockDurationMinutes,
            CurrentWhitelist.EntryCount);

        _state.Load();
        _log.LogInformation("Loaded {Count} state record(s) from {Path}",
            _state.Count, _state.ResolvedPath);

        TrySyncFirewall();

        var stateDir = Path.GetDirectoryName(_state.ResolvedPath);
        if (string.IsNullOrEmpty(stateDir)) stateDir = Directory.GetCurrentDirectory();

        var securityBookmark = new BookmarkStore(Path.Combine(stateDir, "bookmark-security.xml"));
        var rdpBookmark = new BookmarkStore(Path.Combine(stateDir, "bookmark-rdpcorets.xml"));

        SecurityEventSubscriber? security = null;
        RdpCoreTsSubscriber? rdp = null;

        try
        {
            security = new SecurityEventSubscriber(
                securityBookmark,
                _channel.Writer,
                _loggerFactory.CreateLogger<SecurityEventSubscriber>(),
                acceptNlaNtlm: _options.EvaluateNlaFallback);
            security.Start();

            if (_options.EvaluateNlaFallback)
            {
                rdp = new RdpCoreTsSubscriber(
                    rdpBookmark,
                    _channel.Writer,
                    _loggerFactory.CreateLogger<RdpCoreTsSubscriber>());
                rdp.Start();
            }
            else
            {
                _log.LogInformation("EvaluateNlaFallback=false; RdpCoreTS subscription disabled");
            }

            var consumer = ConsumeAsync(stoppingToken);
            var unblockLoop = _unblock.RunAsync(UnblockInterval, stoppingToken);

            await Task.WhenAll(consumer, unblockLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _channel.Writer.TryComplete();
            security?.Dispose();
            rdp?.Dispose();
            _log.LogInformation("BlockRdpBruteForce stopped");
        }
    }

    private void TrySyncFirewall()
    {
        try
        {
            var result = _sync.Sync(DateTime.UtcNow);
            _log.LogInformation(
                "Firewall sync complete: {Total} active, {Missing} added, {Extra} removed",
                result.Total, result.Missing, result.Extra);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Initial firewall sync failed");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var failure in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await ProcessAsync(failure, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(FailedLogon failure, CancellationToken ct)
    {
        try
        {
            if (CurrentWhitelist.IsWhitelisted(failure.Ip))
            {
                _log.LogDebug("Skipping whitelisted IP {Ip}", failure.Ip);
                return;
            }

            var breached = _tracker.Record(failure.Ip, failure.UtcTime);
            _log.LogDebug(
                "Failure recorded: ip={Ip} user={User} source={Source} breached={Breached}",
                failure.Ip, failure.User, failure.Source, breached);

            if (!breached) return;

            var paused = GetPausedUntilUtc();
            if (paused.HasValue && paused.Value > DateTime.UtcNow)
            {
                _log.LogInformation(
                    "Service paused until {Until}; not blocking {Ip} (would block at threshold)",
                    paused.Value, failure.Ip);
                return;
            }

            await BlockAsync(failure.Ip, failure.UtcTime, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to process failure for {Ip}", failure.Ip);
        }
    }

    private async Task BlockAsync(IPAddress ip, DateTime utcNow, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _state.TryGet(ip);
            if (existing is { BlockedUntilUtc: { } until } && until > utcNow)
            {
                _log.LogDebug("IP {Ip} already blocked until {Until}", ip, until);
                return;
            }
            if (existing is { BlockedUntilUtc: null } && existing.LastSeenUtc > DateTime.MinValue)
            {
                _log.LogDebug("IP {Ip} already permanently blocked", ip);
                return;
            }

            TimeSpan? duration = _options.BlockDurationMinutes <= 0
                ? null
                : TimeSpan.FromMinutes(_options.BlockDurationMinutes);

            var record = _state.Upsert(ip, utcNow, duration);
            _firewall.AddIp(ip);
            _state.Save();
            _tracker.Reset(ip);

            _log.LogWarning(
                "Blocked {Ip} (failures={Count}, until={Until})",
                ip, record.Count,
                record.BlockedUntilUtc?.ToString("o") ?? "permanent");
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhitelistEvaluator CurrentWhitelist => Volatile.Read(ref _whitelist);

    public StatusPayload GetStatus() => new()
    {
        ServiceName = "BlockRdpBruteForce",
        FailureThreshold = _options.FailureThreshold,
        SlidingWindowMinutes = _options.SlidingWindowMinutes,
        BlockDurationMinutes = _options.BlockDurationMinutes,
        FirewallRuleName = _options.FirewallRuleName,
        BlockedIpCount = _state.ActiveBlockedIps(DateTime.UtcNow).Count,
        WhitelistEntryCount = CurrentWhitelist.EntryCount,
        EvaluateNlaFallback = _options.EvaluateNlaFallback,
        NowUtc = DateTime.UtcNow,
        StartedUtc = _startedUtc,
        PausedUntilUtc = GetPausedUntilUtc(),
    };

    public IReadOnlyList<IpEntry> GetList() => _state.Snapshot()
        .Select(r => new IpEntry
        {
            Ip = r.Ip,
            Count = r.Count,
            FirstSeenUtc = r.FirstSeenUtc,
            LastSeenUtc = r.LastSeenUtc,
            BlockedUntilUtc = r.BlockedUntilUtc,
        })
        .ToList();

    public async Task<UnblockPayload> UnblockAsync(IPAddress ip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ip);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var utcNow = DateTime.UtcNow;
            var existing = _state.TryGet(ip);
            var was = existing is not null
                && (existing.BlockedUntilUtc is null || existing.BlockedUntilUtc > utcNow);
            if (was)
            {
                _state.MarkExpired(ip, utcNow);
                _firewall.SetIps(_state.ActiveBlockedIps(utcNow));
                _state.Save();
                _tracker.Reset(ip);
                _log.LogWarning("Manually unblocked {Ip} (history retained)", ip);
            }
            return new UnblockPayload { Ip = ip.ToString(), WasBlocked = was };
        }
        finally
        {
            _gate.Release();
        }
    }

    public PausePayload Pause(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return Resume();
        var until = DateTime.UtcNow + duration;
        Interlocked.Exchange(ref _pauseUntilUtcTicks, until.Ticks);
        _log.LogWarning("Blocking paused until {Until}", until);
        return new PausePayload { PausedUntilUtc = until };
    }

    public PausePayload Resume()
    {
        Interlocked.Exchange(ref _pauseUntilUtcTicks, 0);
        _log.LogInformation("Blocking resumed");
        return new PausePayload { PausedUntilUtc = null };
    }

    private DateTime? GetPausedUntilUtc()
    {
        var ticks = Interlocked.Read(ref _pauseUntilUtcTicks);
        if (ticks == 0) return null;
        var until = new DateTime(ticks, DateTimeKind.Utc);
        return until > DateTime.UtcNow ? until : null;
    }

    public ConfigPayload GetConfig()
    {
        if (_settings is null) throw new InvalidOperationException("SettingsWriter not configured");
        return _settings.GetEffective();
    }

    public ConfigSetResult SetConfig(ConfigPayload payload, string callerName)
    {
        if (_settings is null) throw new InvalidOperationException("SettingsWriter not configured");
        ArgumentNullException.ThrowIfNull(payload);

        var result = _settings.Apply(payload, callerName);

        if (result.AppliedHot.Contains("whitelist") && result.Effective.Whitelist is { } entries)
        {
            ApplyWhitelistHot(entries);
        }
        return result;
    }

    private void ApplyWhitelistHot(IReadOnlyList<string> entries)
    {
        var next = new WhitelistEvaluator(entries);
        var prev = Interlocked.Exchange(ref _whitelist, next);
        _log.LogInformation(
            "Whitelist hot-applied (entries: {Old} -> {New})",
            prev?.EntryCount ?? 0, next.EntryCount);
    }
}
