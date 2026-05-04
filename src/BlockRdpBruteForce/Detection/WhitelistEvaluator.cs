using System.Net;
using System.Net.Sockets;

namespace BlockRdpBruteForce.Detection;

public sealed class WhitelistEvaluator
{
    private readonly List<Entry> _entries;

    public WhitelistEvaluator(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = new List<Entry>();
        foreach (var raw in entries)
        {
            if (TryParse(raw, out var network, out var prefix, out var family))
                _entries.Add(new Entry(network, prefix, family));
        }
    }

    public int EntryCount => _entries.Count;

    public bool IsWhitelisted(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var bytes = ip.GetAddressBytes();
        foreach (var entry in _entries)
        {
            if (entry.Family != ip.AddressFamily) continue;
            if (Matches(bytes, entry.Network, entry.Prefix)) return true;
        }
        return false;
    }

    public static bool TryParse(string? entry, out byte[] network, out int prefix, out AddressFamily family)
    {
        network = Array.Empty<byte>();
        prefix = 0;
        family = AddressFamily.Unspecified;

        if (string.IsNullOrWhiteSpace(entry)) return false;
        var s = entry.Trim();

        IPAddress? ip;
        int pfx;
        var slash = s.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(s, out ip)) return false;
            pfx = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        }
        else
        {
            if (slash == 0 || slash == s.Length - 1) return false;
            if (!IPAddress.TryParse(s.AsSpan(0, slash), out ip)) return false;
            if (!int.TryParse(s.AsSpan(slash + 1), out pfx)) return false;
            var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (pfx < 0 || pfx > max) return false;
        }

        network = ip.GetAddressBytes();
        CanonicalizeNetwork(network, pfx);
        prefix = pfx;
        family = ip.AddressFamily;
        return true;
    }

    private static void CanonicalizeNetwork(byte[] addr, int prefix)
    {
        for (var i = 0; i < addr.Length; i++)
        {
            var bitsFromPrefix = Math.Clamp(prefix - i * 8, 0, 8);
            var bitsToZero = 8 - bitsFromPrefix;
            if (bitsToZero == 0) continue;
            if (bitsToZero == 8) { addr[i] = 0; continue; }
            var mask = (byte)(0xFF << bitsToZero);
            addr[i] = (byte)(addr[i] & mask);
        }
    }

    private static bool Matches(byte[] ipBytes, byte[] network, int prefix)
    {
        if (ipBytes.Length != network.Length) return false;
        var fullBytes = prefix / 8;
        var extraBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != network[i]) return false;
        if (extraBits > 0 && fullBytes < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - extraBits));
            if ((ipBytes[fullBytes] & mask) != network[fullBytes]) return false;
        }
        return true;
    }

    private readonly record struct Entry(byte[] Network, int Prefix, AddressFamily Family);
}
