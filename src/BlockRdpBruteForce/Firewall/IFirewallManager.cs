using System.Net;

namespace BlockRdpBruteForce.Firewall;

public interface IFirewallManager
{
    void AddIp(IPAddress ip);
    void RemoveIp(IPAddress ip);
    void SetIps(IEnumerable<IPAddress> ips);
    IReadOnlyCollection<IPAddress> GetBlockedIps();
}
