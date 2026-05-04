using System.Net;
using System.Net.Sockets;

namespace BlockRdpBruteForce.Firewall;

public sealed record FirewallRuleChunk(string RuleName, AddressFamily Family, IReadOnlyList<IPAddress> Addresses);

public static class FirewallRuleChunker
{
    public static IReadOnlyList<FirewallRuleChunk> Chunk(IEnumerable<IPAddress> ips, string baseName, int maxPerRule)
    {
        ArgumentNullException.ThrowIfNull(ips);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (maxPerRule < 1) throw new ArgumentOutOfRangeException(nameof(maxPerRule));

        var v4 = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        foreach (var ip in ips)
        {
            if (ip is null) continue;
            if (!seen.Add(ip)) continue;
            switch (ip.AddressFamily)
            {
                case AddressFamily.InterNetwork: v4.Add(ip); break;
                case AddressFamily.InterNetworkV6: v6.Add(ip); break;
            }
        }

        v4.Sort(IpComparer.Instance);
        v6.Sort(IpComparer.Instance);

        var chunks = new List<FirewallRuleChunk>();
        chunks.AddRange(MakeChunks(v4, AddressFamily.InterNetwork, $"{baseName}-v4", maxPerRule));
        chunks.AddRange(MakeChunks(v6, AddressFamily.InterNetworkV6, $"{baseName}-v6", maxPerRule));
        return chunks;
    }

    public static string FormatRemoteAddresses(IEnumerable<IPAddress> addresses) =>
        string.Join(",", addresses.Select(a => a.ToString()));

    private static IEnumerable<FirewallRuleChunk> MakeChunks(
        List<IPAddress> ips, AddressFamily family, string familyBaseName, int maxPerRule)
    {
        if (ips.Count == 0) yield break;
        var chunkIndex = 1;
        for (var offset = 0; offset < ips.Count; offset += maxPerRule)
        {
            var count = Math.Min(maxPerRule, ips.Count - offset);
            var slice = ips.GetRange(offset, count);
            var name = chunkIndex == 1 ? familyBaseName : $"{familyBaseName}-{chunkIndex}";
            yield return new FirewallRuleChunk(name, family, slice);
            chunkIndex++;
        }
    }

    private sealed class IpComparer : IComparer<IPAddress>
    {
        public static readonly IpComparer Instance = new();
        public int Compare(IPAddress? x, IPAddress? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var xb = x.GetAddressBytes();
            var yb = y.GetAddressBytes();
            if (xb.Length != yb.Length) return xb.Length.CompareTo(yb.Length);
            for (var i = 0; i < xb.Length; i++)
                if (xb[i] != yb[i]) return xb[i].CompareTo(yb[i]);
            return 0;
        }
    }
}
