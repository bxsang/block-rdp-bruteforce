using System.Reflection;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class UpdateApplyCompletionService : BackgroundService
{
    private readonly UpdateStateStore _store;
    private readonly InstalledVariantDetector _variant;
    private readonly InteractiveProcessLauncher _launcher;
    private readonly ILogger<UpdateApplyCompletionService> _log;

    public UpdateApplyCompletionService(
        UpdateStateStore store,
        InstalledVariantDetector variant,
        InteractiveProcessLauncher launcher,
        ILogger<UpdateApplyCompletionService> log)
    {
        _store = store;
        _variant = variant;
        _launcher = launcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief stagger so the host is fully up before we do post-upgrade work.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        try
        {
            var marker = _store.ReadMarker();
            if (marker is null) return;

            var current = _variant.CurrentVersion;
            var stale = IsMarkerStale(marker, DateTime.UtcNow);

            if (UpdateChecker.TryParseVersion(marker.TargetVersion, out var target) &&
                current >= target)
            {
                _log.LogInformation(
                    "Update apply succeeded: now running {Current} (target was {Target})",
                    current, target);

                _store.Update(s =>
                {
                    s.LastAppliedVersion = marker.TargetVersion;
                    s.LastAppliedUtc = DateTime.UtcNow;
                    s.LastApplyError = null;
                });

                TryLaunchTray();
                _store.DeleteMarker();
            }
            else if (stale)
            {
                var msg = $"Update to {marker.TargetVersion} did not complete (current {current}); marker stale";
                _log.LogWarning(msg);
                _store.Update(s => s.LastApplyError = msg);
                _store.DeleteMarker();
            }
            else
            {
                _log.LogInformation(
                    "Update marker present (target {Target}); current {Current} is older — leaving marker for retry",
                    marker.TargetVersion, current);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "UpdateApplyCompletionService failed");
        }
    }

    // Stage-aware staleness: a "launched" marker that never progressed probably
    // means the updater never started — give up after 15 min. Once we've seen
    // download/install activity, allow up to 1h for slow installs. Null stage
    // is treated as "launched" so legacy pre-1.4 markers still time out.
    internal static bool IsMarkerStale(UpdateApplyingMarker marker, DateTime nowUtc)
    {
        var stage = string.IsNullOrEmpty(marker.Stage)
            ? UpdateApplyingMarker.StageLaunched
            : marker.Stage;
        var threshold = stage == UpdateApplyingMarker.StageLaunched
            ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromHours(1);
        return (nowUtc - marker.StartedUtc) > threshold;
    }

    private void TryLaunchTray()
    {
        try
        {
            var serviceExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(serviceExe)) return;

            var dir = Path.GetDirectoryName(serviceExe);
            if (string.IsNullOrEmpty(dir)) return;

            var trayExe = Path.Combine(dir, "BlockRdpBruteForce.Tray.exe");
            if (!File.Exists(trayExe))
            {
                _log.LogInformation("Tray exe not found at {Path}; skipping post-upgrade launch", trayExe);
                return;
            }

            var result = _launcher.LaunchAsActiveUser(trayExe, string.Empty, requestElevation: false);
            if (result.Ok)
            {
                _log.LogInformation("Re-launched tray in user session (pid={Pid})", result.ProcessId);
            }
            else
            {
                _log.LogInformation(
                    "Could not re-launch tray after update ({Error}); HKLM Run will start it at next user logon",
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tray relaunch failed");
        }
    }
}
