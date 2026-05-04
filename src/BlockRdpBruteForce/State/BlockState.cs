namespace BlockRdpBruteForce.State;

public sealed class BlockState
{
    public List<IpRecord> Ips { get; set; } = new();
}

public sealed class IpRecord
{
    public string Ip { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? BlockedUntilUtc { get; set; }
}
