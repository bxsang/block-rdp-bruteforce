using System.Runtime.Versioning;
using System.Text.Json;
using BlockRdpBruteForce.Configuration;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class UpdateStateStore
{
    private const string StateFileName = "update-state.json";
    private const string MarkerFileName = "update-applying.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly ILogger<UpdateStateStore> _log;

    private UpdateStateRecord _record = new();

    public UpdateStateStore(IOptions<AppOptions> options, ILogger<UpdateStateStore> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _dir = ResolveDir(options.Value.UpdateDataPath);
        _log = log;

        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to create update directory {Dir}", _dir); }

        Load();
    }

    public string DirectoryPath => _dir;
    public string StatePath => Path.Combine(_dir, StateFileName);
    public string MarkerPath => Path.Combine(_dir, MarkerFileName);

    public UpdateStateRecord Get()
    {
        lock (_lock) return Clone(_record);
    }

    public void Update(Action<UpdateStateRecord> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_lock)
        {
            mutate(_record);
            WriteAtomic(StatePath, _record);
        }
    }

    public UpdateApplyingMarker? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var json = File.ReadAllText(MarkerPath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<UpdateApplyingMarker>(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read update marker {Path}", MarkerPath);
            return null;
        }
    }

    public void WriteMarker(UpdateApplyingMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        WriteAtomic(MarkerPath, marker);
    }

    public void DeleteMarker()
    {
        try
        {
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete update marker {Path}", MarkerPath);
        }
    }

    public void PruneOldMsis(string? keepFileName)
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var path in Directory.EnumerateFiles(_dir, "*.msi"))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(keepFileName) &&
                    string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(path); }
                catch (Exception ex) { _log.LogDebug(ex, "Could not delete old MSI {Path}", path); }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to prune old MSIs in {Dir}", _dir);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var json = File.ReadAllText(StatePath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var rec = JsonSerializer.Deserialize<UpdateStateRecord>(json);
            if (rec is not null) _record = rec;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load update state from {Path}; starting fresh", StatePath);
        }
    }

    private void WriteAtomic<T>(string path, T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(value, JsonOpts);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write update file {Path}", path);
        }
    }

    private static UpdateStateRecord Clone(UpdateStateRecord r) => new()
    {
        LastCheckUtc = r.LastCheckUtc,
        LatestVersion = r.LatestVersion,
        LatestReleaseUrl = r.LatestReleaseUrl,
        MsiAssetName = r.MsiAssetName,
        MsiAssetUrl = r.MsiAssetUrl,
        MsiAssetSize = r.MsiAssetSize,
        MsiDownloadedPath = r.MsiDownloadedPath,
        LastCheckErrorUtc = r.LastCheckErrorUtc,
        LastCheckError = r.LastCheckError,
        LastApplyAttemptUtc = r.LastApplyAttemptUtc,
        LastAppliedVersion = r.LastAppliedVersion,
        LastAppliedUtc = r.LastAppliedUtc,
        LastApplyError = r.LastApplyError,
    };

    private static string ResolveDir(string raw)
    {
        var resolved = string.IsNullOrWhiteSpace(raw)
            ? @"%ProgramData%\BlockRdpBruteForce\updates"
            : raw;
        return Environment.ExpandEnvironmentVariables(resolved);
    }
}
