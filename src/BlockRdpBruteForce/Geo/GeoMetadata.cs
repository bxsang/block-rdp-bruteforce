namespace BlockRdpBruteForce.Geo;

public sealed class GeoMetadata
{
    public DateTime? LastRefreshUtc { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
    public long DbBytes { get; set; }
    public DateTime? DbModifiedUtc { get; set; }
}
