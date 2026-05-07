using System.Net.Http;
using System.Runtime.Versioning;

namespace BlockRdpBruteForce.Updater;

// Streaming HTTP download with IProgress reporting. Logic mirrors
// GitHubReleaseClient.DownloadAssetAsync in the service project — keep them in
// sync if changing User-Agent or size sanity checks.
[SupportedOSPlatform("windows")]
internal sealed class MsiDownloader
{
    private const string UserAgent = "BlockRdpBruteForce-Updater/1.x";
    private const long MinExpectedSize = 100_000;

    private readonly HttpClient _http;

    public MsiDownloader(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public static MsiDownloader CreateDefault()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return new MsiDownloader(http);
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string targetPath,
        long expectedSize,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { return DownloadResult.Failed($"Could not create directory: {ex.Message}"); }
        }

        var tmp = targetPath + ".tmp";
        var startedUtc = DateTime.UtcNow;

        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return DownloadResult.Failed($"HTTP {(int)response.StatusCode} downloading MSI");

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long bytesRead = 0;
                int n;
                var lastReport = DateTime.UtcNow;

                while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    bytesRead += n;

                    var now = DateTime.UtcNow;
                    if (progress is not null && (now - lastReport).TotalMilliseconds >= 200)
                    {
                        var elapsed = (now - startedUtc).TotalSeconds;
                        var bps = elapsed > 0 ? bytesRead / elapsed : 0;
                        progress.Report(new DownloadProgress(bytesRead, totalBytes, bps));
                        lastReport = now;
                    }
                }

                if (progress is not null)
                {
                    var elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    var bps = elapsed > 0 ? bytesRead / elapsed : 0;
                    progress.Report(new DownloadProgress(bytesRead, totalBytes, bps));
                }
            }

            var size = new FileInfo(tmp).Length;
            if (size < MinExpectedSize)
            {
                TryDelete(tmp);
                return DownloadResult.Failed($"Downloaded MSI too small ({size:N0} bytes); refusing to install");
            }

            if (File.Exists(targetPath))
                File.Replace(tmp, targetPath, destinationBackupFileName: null);
            else
                File.Move(tmp, targetPath);

            return DownloadResult.Success(size);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return DownloadResult.Failed(ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

internal readonly record struct DownloadProgress(long BytesRead, long TotalBytes, double BytesPerSecond);

internal sealed class DownloadResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public long Bytes { get; init; }

    public static DownloadResult Success(long bytes) => new() { Ok = true, Bytes = bytes };
    public static DownloadResult Failed(string error) => new() { Ok = false, Error = error };
}
