using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Configuration;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class UpdateApplier
{
    private const string UpdaterExeName = "BlockRdpBruteForce.Updater.exe";

    private static readonly TimeSpan ApplyRateLimit = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<AppOptions> _options;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly InstalledVariantDetector _variant;
    private readonly UpdateStateStore _store;
    private readonly InteractiveProcessLauncher _launcher;
    private readonly ILogger<UpdateApplier> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTime _lastApplyAttemptUtc = DateTime.MinValue;

    public UpdateApplier(
        IOptionsMonitor<AppOptions> options,
        GitHubReleaseClient releaseClient,
        InstalledVariantDetector variant,
        UpdateStateStore store,
        InteractiveProcessLauncher launcher,
        ILogger<UpdateApplier> log)
    {
        _options = options;
        _releaseClient = releaseClient;
        _variant = variant;
        _store = store;
        _launcher = launcher;
        _log = log;
    }

    public async Task<ApplyResult> ApplyAsync(string requestedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion))
            return ApplyResult.Failed("requestedVersion is required");

        if (!UpdateChecker.TryParseVersion(requestedVersion, out var requested))
            return ApplyResult.Failed($"Invalid version: {requestedVersion}");

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return ApplyResult.Failed("update apply already in progress");

        try
        {
            var sinceLast = DateTime.UtcNow - _lastApplyAttemptUtc;
            if (sinceLast < ApplyRateLimit)
            {
                var wait = (int)Math.Ceiling((ApplyRateLimit - sinceLast).TotalSeconds);
                return ApplyResult.Failed($"rate-limited; try again in {wait} seconds");
            }

            var current = _variant.CurrentVersion;
            if (requested <= current)
                return ApplyResult.Failed(
                    $"Requested version {requested} is not newer than current {current}");

            var record = _store.Get();
            if (!string.Equals(record.LatestVersion, requested.ToString(),
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(record.LatestVersion, $"{requested.Major}.{requested.Minor}.{requested.Build}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ApplyResult.Failed(
                    $"Requested version {requested} does not match cached release {record.LatestVersion ?? "(none)"}; " +
                    "run a check first");
            }

            if (string.IsNullOrWhiteSpace(record.MsiAssetUrl) ||
                string.IsNullOrWhiteSpace(record.MsiAssetName))
            {
                return ApplyResult.Failed("Cached release has no MSI asset URL/name");
            }

            _lastApplyAttemptUtc = DateTime.UtcNow;
            _store.Update(s => s.LastApplyAttemptUtc = _lastApplyAttemptUtc);

            var msiPath = Path.Combine(_store.DirectoryPath, record.MsiAssetName!);
            var logPath = Path.Combine(_store.DirectoryPath, $"msiexec-{requested}.log");

            var marker = new UpdateApplyingMarker
            {
                TargetVersion = requested.ToString(),
                StartedUtc = DateTime.UtcNow,
                MsiPath = msiPath,
                LaunchedInUserSession = false,
                Stage = UpdateApplyingMarker.StageLaunched,
                StageUpdatedUtc = DateTime.UtcNow,
            };

            // Active-session path: stage the updater into ProgramData and launch
            // it in the user's session with the linked elevated token. The
            // updater owns the download + msiexec progress UI and outlives the
            // file replacement that kills both service and tray.
            var stagedUpdater = TryStageUpdater(out var stageError);
            if (stagedUpdater is not null)
            {
                var updaterArgs = BuildUpdaterArgs(record, msiPath, logPath, requested);

                _log.LogWarning(
                    "Applying update {Target} (current {Current}); spawning updater {Path}",
                    requested, current, stagedUpdater);

                var launch = _launcher.LaunchAsActiveUser(stagedUpdater, updaterArgs, requestElevation: true);

                if (launch.Ok)
                {
                    marker.LaunchedInUserSession = true;
                    _store.WriteMarker(marker);
                    _log.LogInformation(
                        "Updater spawned in user session (pid={Pid}, elevated={Elevated})",
                        launch.ProcessId, launch.ProcessElevated);
                    return ApplyResult.Started($"updater started (pid {launch.ProcessId})");
                }

                _log.LogWarning(
                    "Interactive updater launch failed ({Error}); falling back to silent install in session 0",
                    launch.Error);
            }
            else
            {
                _log.LogWarning(
                    "Could not stage updater binary ({Error}); falling back to silent msiexec in session 0",
                    stageError);
            }

            // No-session / staging-failed fallback: download the MSI here and
            // run msiexec /qn under SYSTEM. No progress UI, but it still upgrades.
            return await SilentFallbackAsync(record, msiPath, logPath, marker, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? TryStageUpdater(out string? error)
    {
        error = null;
        try
        {
            var serviceDir = Path.GetDirectoryName(
                Process.GetCurrentProcess().MainModule?.FileName)
                ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(serviceDir))
            {
                error = "could not resolve service install directory";
                return null;
            }

            var sourceUpdater = Path.Combine(serviceDir, UpdaterExeName);
            if (!File.Exists(sourceUpdater))
            {
                error = $"updater not found at {sourceUpdater}";
                return null;
            }

            var stageDir = Path.Combine(_store.DirectoryPath, "stage");
            Directory.CreateDirectory(stageDir);
            var stagedPath = Path.Combine(stageDir, UpdaterExeName);

            // Always overwrite — a future updater build should be copied fresh.
            File.Copy(sourceUpdater, stagedPath, overwrite: true);
            return stagedPath;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string BuildUpdaterArgs(
        UpdateStateRecord record, string msiPath, string logPath, Version requested)
    {
        // Quote each value to defend against spaces in ProgramData paths or asset names.
        return string.Join(' ',
            "--version",    Quote(requested.ToString()),
            "--asset-name", Quote(record.MsiAssetName ?? string.Empty),
            "--asset-url",  Quote(record.MsiAssetUrl ?? string.Empty),
            "--asset-size", record.MsiAssetSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--msi-path",   Quote(msiPath),
            "--log-path",   Quote(logPath));
    }

    private static string Quote(string s) => $"\"{s}\"";

    private async Task<ApplyResult> SilentFallbackAsync(
        UpdateStateRecord record,
        string msiPath,
        string logPath,
        UpdateApplyingMarker marker,
        CancellationToken ct)
    {
        if (!File.Exists(msiPath) || new FileInfo(msiPath).Length < 100_000)
        {
            _log.LogInformation("Downloading MSI {Asset} (silent fallback)", record.MsiAssetName);
            var info = new UpdateInfo
            {
                Version = record.LatestVersion!,
                MsiAssetName = record.MsiAssetName!,
                MsiAssetUrl = record.MsiAssetUrl!,
                MsiAssetSize = record.MsiAssetSize,
                ReleaseUrl = record.LatestReleaseUrl ?? string.Empty,
            };
            var dl = await _releaseClient.DownloadAssetAsync(info, msiPath, ct).ConfigureAwait(false);
            if (!dl.Ok)
            {
                _store.Update(s => s.LastApplyError = dl.Error);
                return ApplyResult.Failed($"Download failed: {dl.Error}");
            }
            _store.Update(s => s.MsiDownloadedPath = msiPath);
            _store.PruneOldMsis(record.MsiAssetName);
        }

        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var msiexecPath = Path.Combine(systemDir, "msiexec.exe");
        var args = $"/i \"{msiPath}\" /qn /norestart /L*v \"{logPath}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = msiexecPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            if (proc is null)
                return ApplyResult.Failed("Process.Start returned null");

            marker.LaunchedInUserSession = false;
            marker.Stage = UpdateApplyingMarker.StageInstalling;
            marker.StageUpdatedUtc = DateTime.UtcNow;
            _store.WriteMarker(marker);
            return ApplyResult.Started($"msiexec started silently (pid {proc.Id})");
        }
        catch (Exception ex)
        {
            _store.Update(s => s.LastApplyError = ex.Message);
            return ApplyResult.Failed($"Fallback launch failed: {ex.Message}");
        }
    }
}

public sealed class ApplyResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }

    public static ApplyResult Started(string message) => new() { Ok = true, Message = message };
    public static ApplyResult Failed(string error) => new() { Ok = false, Error = error };
}
