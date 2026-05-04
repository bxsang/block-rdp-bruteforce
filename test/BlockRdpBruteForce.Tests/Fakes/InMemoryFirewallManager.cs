using System.Net;
using BlockRdpBruteForce.Firewall;

namespace BlockRdpBruteForce.Tests.Fakes;

public sealed class InMemoryFirewallManager : IFirewallManager
{
    private readonly object _gate = new();
    private readonly HashSet<IPAddress> _ips = new();

    public int AddCallCount { get; private set; }
    public int RemoveCallCount { get; private set; }
    public int SetCallCount { get; private set; }

    public void AddIp(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        lock (_gate)
        {
            _ips.Add(ip);
            AddCallCount++;
        }
    }

    public void RemoveIp(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        lock (_gate)
        {
            _ips.Remove(ip);
            RemoveCallCount++;
        }
    }

    public void SetIps(IEnumerable<IPAddress> ips)
    {
        ArgumentNullException.ThrowIfNull(ips);
        lock (_gate)
        {
            _ips.Clear();
            foreach (var ip in ips) _ips.Add(ip);
            SetCallCount++;
        }
    }

    public IReadOnlyCollection<IPAddress> GetBlockedIps()
    {
        lock (_gate) return _ips.ToArray();
    }
}
