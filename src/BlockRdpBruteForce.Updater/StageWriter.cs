using System.Runtime.Versioning;
using System.Text.Json;

namespace BlockRdpBruteForce.Updater;

// Writes the update-applying.json marker that the service's
// UpdateApplyCompletionService reads after restart. Schema mirrors
// UpdateApplyingMarker in BlockRdpBruteForce.Update — drift-prone, keep aligned.
[SupportedOSPlatform("windows")]
internal sealed class StageWriter
{
    public const string StageLaunched = "launched";
    public const string StageDownloading = "downloading";
    public const string StageInstalling = "installing";
    public const string StageDone = "done";
    public const string StageFailed = "failed";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _markerPath;
    private readonly string _targetVersion;
    private readonly string _msiPath;
    private readonly DateTime _startedUtc;
    private readonly object _lock = new();

    public StageWriter(string updatesDir, string targetVersion, string msiPath, DateTime startedUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(updatesDir);
        _markerPath = Path.Combine(updatesDir, "update-applying.json");
        _targetVersion = targetVersion;
        _msiPath = msiPath;
        _startedUtc = startedUtc;
    }

    public void Write(string stage, string? error = null)
    {
        lock (_lock)
        {
            try
            {
                var record = new MarkerRecord
                {
                    TargetVersion = _targetVersion,
                    StartedUtc = _startedUtc,
                    MsiPath = _msiPath,
                    LaunchedInUserSession = true,
                    Stage = stage,
                    StageUpdatedUtc = DateTime.UtcNow,
                    LastError = error,
                };
                var dir = Path.GetDirectoryName(_markerPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(record, JsonOpts);
                var tmp = _markerPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_markerPath))
                    File.Replace(tmp, _markerPath, destinationBackupFileName: null);
                else
                    File.Move(tmp, _markerPath);
            }
            catch
            {
                // Marker writes are best-effort; swallow so the user-visible flow keeps going.
            }
        }
    }

    private sealed class MarkerRecord
    {
        public string TargetVersion { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public string MsiPath { get; set; } = string.Empty;
        public bool LaunchedInUserSession { get; set; }
        public string? Stage { get; set; }
        public DateTime? StageUpdatedUtc { get; set; }
        public string? LastError { get; set; }
    }
}
