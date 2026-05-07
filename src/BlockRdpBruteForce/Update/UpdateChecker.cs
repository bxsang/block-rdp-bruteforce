using System.Runtime.Versioning;
using BlockRdpBruteForce.Configuration;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class UpdateChecker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    private readonly IOptionsMonitor<AppOptions> _options;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly InstalledVariantDetector _variant;
    private readonly UpdateStateStore _store;
    private readonly ILogger<UpdateChecker> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateChecker(
        IOptionsMonitor<AppOptions> options,
        GitHubReleaseClient releaseClient,
        InstalledVariantDetector variant,
        UpdateStateStore store,
        ILogger<UpdateChecker> log)
    {
        _options = options;
        _releaseClient = releaseClient;
        _variant = variant;
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stagger initial check so brand-new installs detect updates promptly,
            // but don't block service start.
            try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            using var timer = new PeriodicTimer(TickInterval);
            while (true)
            {
                try
                {
                    await MaybeCheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "UpdateChecker tick failed");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<UpdateStateRecord> CheckNowAsync(CancellationToken ct)
    {
        await DoCheckAsync(force: true, ct).ConfigureAwait(false);
        return _store.Get();
    }

    private async Task MaybeCheckAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.AutoUpdateEnabled) return;

        var record = _store.Get();
        var intervalHours = Math.Max(1, opts.AutoUpdateCheckIntervalHours);
        if (record.LastCheckUtc is { } last &&
            (DateTime.UtcNow - last) < TimeSpan.FromHours(intervalHours))
        {
            return;
        }

        await DoCheckAsync(force: false, ct).ConfigureAwait(false);
    }

    private async Task DoCheckAsync(bool force, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _log.LogInformation("Update check already in progress; ignoring concurrent request");
            return;
        }

        try
        {
            var opts = _options.CurrentValue;
            _log.LogInformation(
                "Checking GitHub for updates ({Owner}/{Repo}, variant={Variant})",
                opts.UpdateRepoOwner, opts.UpdateRepoName, _variant.Variant);

            var result = await _releaseClient
                .FetchLatestAsync(opts.UpdateRepoOwner, opts.UpdateRepoName, _variant.Variant, ct)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;

            if (!result.Ok || result.Info is null)
            {
                _store.Update(s =>
                {
                    s.LastCheckUtc = now;
                    s.LastCheckErrorUtc = now;
                    s.LastCheckError = result.Error;
                });
                _log.LogWarning("Update check failed: {Error}", result.Error);
                return;
            }

            var info = result.Info;
            _store.Update(s =>
            {
                s.LastCheckUtc = now;
                s.LastCheckErrorUtc = null;
                s.LastCheckError = null;
                s.LatestVersion = info.Version;
                s.LatestReleaseUrl = info.ReleaseUrl;
                s.MsiAssetName = info.MsiAssetName;
                s.MsiAssetUrl = info.MsiAssetUrl;
                s.MsiAssetSize = info.MsiAssetSize;
            });

            if (TryParseVersion(info.Version, out var latest) &&
                latest > _variant.CurrentVersion)
            {
                _log.LogInformation(
                    "Update available: {Latest} (currently {Current})",
                    info.Version, _variant.CurrentVersionString);
            }
            else
            {
                _log.LogInformation(
                    "No update needed: latest={Latest}, current={Current}",
                    info.Version, _variant.CurrentVersionString);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool TryParseVersion(string s, out Version version)
    {
        // GitHub tags arrive without the leading 'v' here (already stripped),
        // but be defensive.
        var trimmed = (s ?? string.Empty).Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version!);
    }
}
