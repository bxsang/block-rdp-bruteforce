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
    private readonly ILogger<UpdateApplier> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTime _lastApplyAttemptUtc = DateTime.MinValue;

    public UpdateApplier(
        IOptionsMonitor<AppOptions> options,
        GitHubReleaseClient releaseClient,
        InstalledVariantDetector variant,
        UpdateStateStore store,
        ILogger<UpdateApplier> log)
    {
        _options = options;
        _releaseClient = releaseClient;
        _variant = variant;
        _store = store;
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

            // Stage the updater binary into ProgramData and hand the path + args
            // back to the tray. The tray ShellExecutes it with the "runas" verb,
            // which produces a standard UAC prompt and runs the updater elevated
            // inside the user's session. Service-driven CreateProcessAsUser from
            // session 0 hits STATUS_DLL_INIT_FAILED on modern Windows when the
            // process tries to attach to the target session's window station.
            var stagedUpdater = TryStageUpdater(out var serviceDir, out var stageError);
            if (stagedUpdater is null)
                return ApplyResult.Failed($"Could not stage updater: {stageError}");

            var trayPath = Path.Combine(serviceDir!, "BlockRdpBruteForce.Tray.exe");
            var updaterArgs = BuildUpdaterArgs(record, msiPath, logPath, requested, trayPath);

            var marker = new UpdateApplyingMarker
            {
                TargetVersion = requested.ToString(),
                StartedUtc = DateTime.UtcNow,
                MsiPath = msiPath,
                LaunchedInUserSession = true,
                Stage = UpdateApplyingMarker.StageLaunched,
                StageUpdatedUtc = DateTime.UtcNow,
            };
            _store.WriteMarker(marker);

            _log.LogInformation(
                "Update {Target} staged at {Path}; tray will elevate via UAC (current {Current})",
                requested, stagedUpdater, current);

            return ApplyResult.Staged(stagedUpdater, updaterArgs);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? TryStageUpdater(out string? serviceDir, out string? error)
    {
        error = null;
        serviceDir = null;
        try
        {
            serviceDir = Path.GetDirectoryName(
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
        UpdateStateRecord record, string msiPath, string logPath, Version requested, string trayPath)
    {
        // Quote each value to defend against spaces in ProgramData paths or asset names.
        return string.Join(' ',
            "--version",    Quote(requested.ToString()),
            "--asset-name", Quote(record.MsiAssetName ?? string.Empty),
            "--asset-url",  Quote(record.MsiAssetUrl ?? string.Empty),
            "--asset-size", record.MsiAssetSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--msi-path",   Quote(msiPath),
            "--log-path",   Quote(logPath),
            "--tray-path",  Quote(trayPath));
    }

    private static string Quote(string s) => $"\"{s}\"";

}

public sealed class ApplyResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string? UpdaterPath { get; init; }
    public string? UpdaterArgs { get; init; }

    public static ApplyResult Staged(string updaterPath, string updaterArgs) =>
        new()
        {
            Ok = true,
            Message = "updater staged",
            UpdaterPath = updaterPath,
            UpdaterArgs = updaterArgs,
        };

    public static ApplyResult Failed(string error) => new() { Ok = false, Error = error };
}
