namespace BlockRdpBruteForce.Geo;

public sealed class GeoDownloader
{
    private const long MinValidBytes = 1_000_000;
    private const string DownloadUrl = "https://ipinfo.io/data/ipinfo_lite.mmdb";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GeoDownloader> _log;

    public GeoDownloader(IHttpClientFactory httpFactory, ILogger<GeoDownloader> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<GeoDownloadResult> DownloadAsync(string token, string targetPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return GeoDownloadResult.Failed("IPinfo token not configured");
        if (string.IsNullOrWhiteSpace(targetPath))
            return GeoDownloadResult.Failed("Target path not configured");

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { return GeoDownloadResult.Failed($"Could not create geo directory: {ex.Message}"); }
        }

        var url = $"{DownloadUrl}?token={Uri.EscapeDataString(token)}";
        var tmp = targetPath + ".tmp";

        try
        {
            using var http = _httpFactory.CreateClient("GeoDownloader");
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                string body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch { }
                return GeoDownloadResult.Failed($"HTTP {status}: {Truncate(body, 200)}");
            }

            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var size = new FileInfo(tmp).Length;
            if (size < MinValidBytes)
            {
                TryDelete(tmp);
                return GeoDownloadResult.Failed($"Downloaded file is too small ({size:N0} bytes); refusing to install");
            }

            if (File.Exists(targetPath))
                File.Replace(tmp, targetPath, destinationBackupFileName: null);
            else
                File.Move(tmp, targetPath);

            return GeoDownloadResult.Success(size);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            _log.LogWarning(ex, "Geo download failed");
            return GeoDownloadResult.Failed(ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);
}

public sealed class GeoDownloadResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public long Bytes { get; init; }

    public static GeoDownloadResult Success(long bytes) => new() { Ok = true, Bytes = bytes };
    public static GeoDownloadResult Failed(string error) => new() { Ok = false, Error = error };
}
