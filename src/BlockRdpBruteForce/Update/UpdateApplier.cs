using System.Diagnostics;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Configuration;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class UpdateApplier
{
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
            if (!File.Exists(msiPath) || new FileInfo(msiPath).Length < 100_000)
            {
                _log.LogInformation("Downloading MSI {Asset}", record.MsiAssetName);
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

            // Spawn msiexec /passive /norestart in the active user session, with
            // the user's linked elevated token so no UAC prompt is required.
            // The current service process will be killed by the MSI MajorUpgrade
            // ServiceControl step — that's expected. UpdateApplyCompletionService
            // picks up the marker on next start to relaunch the tray.
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var msiexecPath = Path.Combine(systemDir, "msiexec.exe");
            var logPath = Path.Combine(_store.DirectoryPath, $"msiexec-{requested}.log");
            var args = $"/i \"{msiPath}\" /passive /norestart /L*v \"{logPath}\"";

            var marker = new UpdateApplyingMarker
            {
                TargetVersion = requested.ToString(),
                StartedUtc = DateTime.UtcNow,
                MsiPath = msiPath,
                LaunchedInUserSession = false,
            };

            _log.LogWarning(
                "Applying update {Target} (current {Current}); spawning msiexec /passive",
                requested, current);

            var launch = _launcher.LaunchAsActiveUser(msiexecPath, args, requestElevation: true);

            if (launch.Ok)
            {
                marker.LaunchedInUserSession = true;
                _store.WriteMarker(marker);
                _log.LogInformation(
                    "msiexec spawned in user session (pid={Pid}, elevated={Elevated})",
                    launch.ProcessId, launch.ProcessElevated);
                return ApplyResult.Started($"msiexec started (pid {launch.ProcessId})");
            }

            // Fallback: launch under SYSTEM in session 0 with /qn (silent). Still
            // succeeds, but no progress UI; tray will be re-launched by the
            // post-upgrade completion service when it runs after a user logs in.
            _log.LogWarning(
                "Interactive launch failed ({Error}); falling back to silent install in session 0",
                launch.Error);
            args = $"/i \"{msiPath}\" /qn /norestart /L*v \"{logPath}\"";

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
                _store.WriteMarker(marker);
                return ApplyResult.Started($"msiexec started silently (pid {proc.Id})");
            }
            catch (Exception ex)
            {
                _store.Update(s => s.LastApplyError = ex.Message);
                return ApplyResult.Failed($"Fallback launch failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
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
