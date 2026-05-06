using System.Runtime.Versioning;
using System.Text.Json;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Ipc;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Geo;

[SupportedOSPlatform("windows")]
public sealed class GeoRefreshService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(24);
    private const string DbFileName = "ipinfo_lite.mmdb";
    private const string MetaFileName = "geo-meta.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly IOptionsMonitor<AppOptions> _optionsMonitor;
    private readonly GeoLookup _lookup;
    private readonly GeoDownloader _downloader;
    private readonly ILogger<GeoRefreshService> _log;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _metaLock = new();
    private readonly string _dbPath;
    private readonly string _metaPath;

    private GeoMetadata _metadata = new();
    private volatile bool _refreshInProgress;

    public GeoRefreshService(
        IOptionsMonitor<AppOptions> optionsMonitor,
        GeoLookup lookup,
        GeoDownloader downloader,
        ILogger<GeoRefreshService> log)
    {
        _optionsMonitor = optionsMonitor;
        _lookup = lookup;
        _downloader = downloader;
        _log = log;

        var geoDir = ResolveGeoDir(optionsMonitor.CurrentValue.GeoDataPath);
        _dbPath = Path.Combine(geoDir, DbFileName);
        _metaPath = Path.Combine(geoDir, MetaFileName);

        try { Directory.CreateDirectory(geoDir); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to create geo directory {Dir}", geoDir); }

        LoadMetadata();
        TryOpenExistingDb();
    }

    public string DbPath => _dbPath;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Run an initial check shortly after startup so a brand-new install
            // gets its DB on first launch (without blocking host start).
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            using var timer = new PeriodicTimer(TickInterval);
            while (true)
            {
                try
                {
                    await MaybeRefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "GeoRefresh tick failed");
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<GeoStatusPayload> RefreshNowAsync(CancellationToken ct)
    {
        var opts = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.IpInfoToken))
        {
            UpdateMetadata(m =>
            {
                m.LastErrorUtc = DateTime.UtcNow;
                m.LastError = "IPinfo token not configured";
            });
            _log.LogWarning("Geo refresh requested but IPinfo token is not configured");
            return GetStatus();
        }
        await DoRefreshAsync(opts, ct).ConfigureAwait(false);
        return GetStatus();
    }

    public GeoStatusPayload GetStatus()
    {
        var opts = _optionsMonitor.CurrentValue;
        GeoMetadata snapshot;
        lock (_metaLock) snapshot = Clone(_metadata);

        long bytes = 0;
        DateTime? mod = null;
        var dbExists = File.Exists(_dbPath);
        if (dbExists)
        {
            try
            {
                var fi = new FileInfo(_dbPath);
                bytes = fi.Length;
                mod = fi.LastWriteTimeUtc;
            }
            catch { }
        }

        return new GeoStatusPayload
        {
            Enabled = opts.GeoLookupEnabled,
            TokenConfigured = !string.IsNullOrWhiteSpace(opts.IpInfoToken),
            DbPresent = dbExists,
            DbBytes = bytes,
            DbModifiedUtc = mod,
            LastRefreshUtc = snapshot.LastRefreshUtc,
            LastErrorUtc = snapshot.LastErrorUtc,
            LastError = snapshot.LastError,
            IntervalDays = opts.GeoRefreshIntervalDays,
            RefreshInProgress = _refreshInProgress,
        };
    }

    private async Task MaybeRefreshAsync(CancellationToken ct)
    {
        var opts = _optionsMonitor.CurrentValue;
        if (!opts.GeoLookupEnabled) return;
        if (string.IsNullOrWhiteSpace(opts.IpInfoToken)) return;

        var interval = TimeSpan.FromDays(Math.Max(1, opts.GeoRefreshIntervalDays));
        var age = DbAge();
        if (age.HasValue && age.Value < interval) return;

        await DoRefreshAsync(opts, ct).ConfigureAwait(false);
    }

    private async Task DoRefreshAsync(AppOptions opts, CancellationToken ct)
    {
        if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _log.LogInformation("Geo refresh already in progress; ignoring concurrent request");
            return;
        }

        try
        {
            _refreshInProgress = true;
            _log.LogInformation("Refreshing geo DB from IPinfo Lite");

            var result = await _downloader.DownloadAsync(opts.IpInfoToken, _dbPath, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                if (_lookup.TryOpen(_dbPath))
                {
                    long bytes;
                    DateTime mod;
                    try
                    {
                        var fi = new FileInfo(_dbPath);
                        bytes = fi.Length;
                        mod = fi.LastWriteTimeUtc;
                    }
                    catch
                    {
                        bytes = result.Bytes;
                        mod = DateTime.UtcNow;
                    }

                    UpdateMetadata(m =>
                    {
                        m.LastRefreshUtc = DateTime.UtcNow;
                        m.DbBytes = bytes;
                        m.DbModifiedUtc = mod;
                        m.LastError = null;
                        m.LastErrorUtc = null;
                    });
                    _log.LogInformation("Geo DB refreshed: {Bytes:N0} bytes", bytes);
                }
                else
                {
                    UpdateMetadata(m =>
                    {
                        m.LastErrorUtc = DateTime.UtcNow;
                        m.LastError = "Download succeeded but reader could not open the file";
                    });
                }
            }
            else
            {
                UpdateMetadata(m =>
                {
                    m.LastErrorUtc = DateTime.UtcNow;
                    m.LastError = result.Error ?? "unknown error";
                });
                _log.LogWarning("Geo DB refresh failed: {Error}", result.Error);
            }
        }
        finally
        {
            _refreshInProgress = false;
            _refreshGate.Release();
        }
    }

    private void TryOpenExistingDb()
    {
        if (File.Exists(_dbPath)) _lookup.TryOpen(_dbPath);
    }

    private TimeSpan? DbAge()
    {
        if (!File.Exists(_dbPath)) return null;
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(_dbPath); }
        catch { return null; }
    }

    private void LoadMetadata()
    {
        try
        {
            if (!File.Exists(_metaPath)) return;
            var json = File.ReadAllText(_metaPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var meta = JsonSerializer.Deserialize<GeoMetadata>(json);
            if (meta is not null) lock (_metaLock) _metadata = meta;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load geo metadata from {Path}", _metaPath);
        }
    }

    private void UpdateMetadata(Action<GeoMetadata> mutate)
    {
        lock (_metaLock)
        {
            mutate(_metadata);
            try
            {
                var dir = Path.GetDirectoryName(_metaPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_metadata, JsonOpts);
                var tmp = _metaPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_metaPath))
                    File.Replace(tmp, _metaPath, destinationBackupFileName: null);
                else
                    File.Move(tmp, _metaPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to save geo metadata to {Path}", _metaPath);
            }
        }
    }

    private static GeoMetadata Clone(GeoMetadata m) => new()
    {
        LastRefreshUtc = m.LastRefreshUtc,
        LastErrorUtc = m.LastErrorUtc,
        LastError = m.LastError,
        DbBytes = m.DbBytes,
        DbModifiedUtc = m.DbModifiedUtc,
    };

    private static string ResolveGeoDir(string raw)
    {
        var resolved = string.IsNullOrWhiteSpace(raw)
            ? @"%ProgramData%\BlockRdpBruteForce\geo"
            : raw;
        return Environment.ExpandEnvironmentVariables(resolved);
    }
}
