namespace BlockRdpBruteForce.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "BlockRdp";

    public int FailureThreshold { get; set; } = 5;
    public int SlidingWindowMinutes { get; set; } = 10;
    public int BlockDurationMinutes { get; set; } = 1440;
    public List<string> Whitelist { get; set; } = new();
    public string FirewallRuleName { get; set; } = "BlockRDPBruteForce";
    public string FirewallScope { get; set; } = "AllPorts";
    public string StateFilePath { get; set; } = @"%ProgramData%\BlockRdpBruteForce\state.json";
    public string LogPath { get; set; } = @"%ProgramData%\BlockRdpBruteForce\logs\service-.log";
    public int MaxRemoteAddressesPerRule { get; set; } = 1000;
    public bool EvaluateNlaFallback { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90;
    public string PipeName { get; set; } = "BlockRdpBruteForce";
}
