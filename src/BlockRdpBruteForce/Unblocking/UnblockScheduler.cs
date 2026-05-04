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

    public UnblockScheduler(IFirewallManager firewall, StateStore state, SemaphoreSlim gate, ILogger<UnblockScheduler> log)
    {
        _firewall = firewall;
        _state = state;
        _gate = gate;
        _log = log;
    }

    public async Task<int> RunOnceAsync(DateTime utcNow, CancellationToken ct = default)
    {
        if (_state.ExpiredIps(utcNow).Count == 0) return 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var expired = _state.ExpiredIps(utcNow);
            if (expired.Count == 0) return 0;

            foreach (var ip in expired)
                _state.Remove(ip);

            _firewall.SetIps(_state.ActiveBlockedIps(utcNow));
            _state.Save();

            _log.LogInformation("Unblocked {Count} expired IPs.", expired.Count);
            return expired.Count;
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
