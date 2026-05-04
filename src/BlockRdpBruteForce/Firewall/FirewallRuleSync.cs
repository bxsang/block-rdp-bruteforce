using System.Net;
using BlockRdpBruteForce.State;
using Microsoft.Extensions.Logging;

namespace BlockRdpBruteForce.Firewall;

public sealed record SyncResult(int Missing, int Extra, int Total);

public sealed class FirewallRuleSync
{
    private readonly IFirewallManager _firewall;
    private readonly StateStore _state;
    private readonly ILogger<FirewallRuleSync> _log;

    public FirewallRuleSync(IFirewallManager firewall, StateStore state, ILogger<FirewallRuleSync> log)
    {
        _firewall = firewall;
        _state = state;
        _log = log;
    }

    public SyncResult Sync(DateTime utcNow)
    {
        var expected = new HashSet<IPAddress>(_state.ActiveBlockedIps(utcNow));
        var actual = new HashSet<IPAddress>(_firewall.GetBlockedIps());

        var missing = expected.Except(actual).Count();
        var extra = actual.Except(expected).Count();

        if (missing == 0 && extra == 0)
        {
            _log.LogInformation("Firewall and state are in sync ({Count} IPs).", expected.Count);
            return new SyncResult(0, 0, expected.Count);
        }

        _log.LogWarning(
            "Reconciling firewall: state has {Expected}, firewall has {Actual}; +{Missing} missing, -{Extra} stale",
            expected.Count, actual.Count, missing, extra);

        _firewall.SetIps(expected);
        return new SyncResult(missing, extra, expected.Count);
    }
}
