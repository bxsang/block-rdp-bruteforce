using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockRdpBruteForce.Update;

[SupportedOSPlatform("windows")]
public sealed class GitHubReleaseClient
{
    public const string HttpClientName = "GitHubUpdates";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubReleaseClient> _log;

    public GitHubReleaseClient(IHttpClientFactory httpFactory, ILogger<GitHubReleaseClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<ReleaseFetchResult> FetchLatestAsync(
        string owner, string repo, MsiVariant variant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return ReleaseFetchResult.Failed("Repo owner/name not configured");

        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest";

        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                string body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch { }
                return ReleaseFetchResult.Failed($"HTTP {status}: {Truncate(body, 200)}");
            }

            var release = await response.Content
                .ReadFromJsonAsync<GitHubReleaseDto>(JsonOpts, ct)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return ReleaseFetchResult.Failed("Release JSON missing tag_name");

            var version = StripVPrefix(release.TagName!);
            var assetSuffix = variant == MsiVariant.SelfContained
                ? "-self-contained.msi"
                : "-framework-dependent.msi";
            var preferredName = $"BlockRdpBruteForce-{version}{assetSuffix}";

            var assets = release.Assets ?? new List<GitHubAssetDto>();
            var asset = assets.FirstOrDefault(a =>
                    string.Equals(a.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                ?? assets.FirstOrDefault(a =>
                    a.Name?.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase) == true);

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return ReleaseFetchResult.Failed(
                    $"Release {release.TagName} has no asset matching '*{assetSuffix}'");

            return ReleaseFetchResult.Success(new UpdateInfo
            {
                Version = version,
                ReleaseUrl = release.HtmlUrl ?? string.Empty,
                MsiAssetName = asset.Name ?? preferredName,
                MsiAssetUrl = asset.BrowserDownloadUrl!,
                MsiAssetSize = asset.Size,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub release fetch failed for {Owner}/{Repo}", owner, repo);
            return ReleaseFetchResult.Failed(ex.Message);
        }
    }

    public async Task<DownloadResult> DownloadAssetAsync(
        UpdateInfo info, string targetPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { return DownloadResult.Failed($"Could not create directory: {ex.Message}"); }
        }

        var tmp = targetPath + ".tmp";

        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var response = await http
                .GetAsync(info.MsiAssetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return DownloadResult.Failed($"HTTP {(int)response.StatusCode} downloading MSI");

            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var size = new FileInfo(tmp).Length;
            // Sanity-check: MSIs should be at least a few hundred KB even for framework-dependent.
            if (size < 100_000)
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
            _log.LogWarning(ex, "MSI download failed");
            return DownloadResult.Failed(ex.Message);
        }
    }

    private static string StripVPrefix(string tag)
    {
        var trimmed = tag.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed[1..]
            : trimmed;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}

public sealed class ReleaseFetchResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public UpdateInfo? Info { get; init; }

    public static ReleaseFetchResult Success(UpdateInfo info) => new() { Ok = true, Info = info };
    public static ReleaseFetchResult Failed(string error) => new() { Ok = false, Error = error };
}

public sealed class DownloadResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public long Bytes { get; init; }

    public static DownloadResult Success(long bytes) => new() { Ok = true, Bytes = bytes };
    public static DownloadResult Failed(string error) => new() { Ok = false, Error = error };
}
