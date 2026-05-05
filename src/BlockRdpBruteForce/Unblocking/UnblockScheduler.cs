using System.Net;
using BlockRdpBruteForce.Firewall;
using BlockRdpBruteForce.State;
using Microsoft.Extensions.Logging;

namespace BlockRdpBruteForce.Unblocking;

public sealed class UnblockScheduler
{
    private readonly IFirewallManager _firewall;
    private readonly StateStore _state;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<UnblockScheduler> _log;
    private readonly int _historyRetentionDays;

    public UnblockScheduler(
        IFirewallManager firewall,
        StateStore state,
        SemaphoreSlim gate,
        ILogger<UnblockScheduler> log,
        int historyRetentionDays = 0)
    {
        if (historyRetentionDays < 0) throw new ArgumentOutOfRangeException(nameof(historyRetentionDays));
        _firewall = firewall;
        _state = state;
        _gate = gate;
        _log = log;
        _historyRetentionDays = historyRetentionDays;
    }

    public async Task<int> RunOnceAsync(DateTime utcNow, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = new HashSet<IPAddress>(_state.ActiveBlockedIps(utcNow));
            var stale = _firewall.GetBlockedIps().Where(ip => !active.Contains(ip)).ToList();
            var stateChanged = false;

            if (stale.Count > 0)
            {
                _firewall.SetIps(active);
                _log.LogInformation("Cleared {Count} expired IPs from firewall (history retained).", stale.Count);
            }

            if (_historyRetentionDays > 0)
            {
                var cutoff = utcNow - TimeSpan.FromDays(_historyRetentionDays);
                var pruned = _state.PruneHistoryOlderThan(cutoff, utcNow);
                if (pruned.Count > 0)
                {
                    stateChanged = true;
                    _log.LogInformation(
                        "Pruned {Count} historical record(s) older than {Days} day(s).",
                        pruned.Count, _historyRetentionDays);
                }
            }

            if (stateChanged) _state.Save();

            return stale.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        using var timer = new PeriodicTimer(interval);
        while (true)
        {
            try
            {
                await RunOnceAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "UnblockScheduler tick failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
