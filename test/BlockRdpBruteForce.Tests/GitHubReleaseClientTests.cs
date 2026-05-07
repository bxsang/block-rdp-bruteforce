using System.Net;
using System.Runtime.Versioning;
using System.Text;
using BlockRdpBruteForce.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task FetchLatest_picks_self_contained_asset()
    {
        var json = """
        {
          "tag_name": "v1.3.0",
          "html_url": "https://github.com/owner/repo/releases/tag/v1.3.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "BlockRdpBruteForce-1.3.0-self-contained.msi",
              "size": 80000000,
              "browser_download_url": "https://example.com/sc.msi" },
            { "name": "BlockRdpBruteForce-1.3.0-framework-dependent.msi",
              "size": 4000000,
              "browser_download_url": "https://example.com/fd.msi" }
          ]
        }
        """;

        var client = MakeClient(json, HttpStatusCode.OK);
        var result = await client.FetchLatestAsync("owner", "repo", MsiVariant.SelfContained, default);

        Assert.True(result.Ok);
        Assert.NotNull(result.Info);
        Assert.Equal("1.3.0", result.Info!.Version);
        Assert.Equal("BlockRdpBruteForce-1.3.0-self-contained.msi", result.Info.MsiAssetName);
        Assert.Equal("https://example.com/sc.msi", result.Info.MsiAssetUrl);
        Assert.Equal(80_000_000, result.Info.MsiAssetSize);
    }

    [Fact]
    public async Task FetchLatest_picks_framework_dependent_asset()
    {
        var json = """
        {
          "tag_name": "1.3.0",
          "html_url": "https://example.com/release",
          "assets": [
            { "name": "BlockRdpBruteForce-1.3.0-self-contained.msi", "size": 80000000,
              "browser_download_url": "https://example.com/sc.msi" },
            { "name": "BlockRdpBruteForce-1.3.0-framework-dependent.msi", "size": 4000000,
              "browser_download_url": "https://example.com/fd.msi" }
          ]
        }
        """;

        var client = MakeClient(json, HttpStatusCode.OK);
        var result = await client.FetchLatestAsync("owner", "repo", MsiVariant.FrameworkDependent, default);

        Assert.True(result.Ok);
        Assert.Equal("BlockRdpBruteForce-1.3.0-framework-dependent.msi", result.Info!.MsiAssetName);
    }

    [Fact]
    public async Task FetchLatest_falls_back_to_suffix_when_exact_name_missing()
    {
        var json = """
        {
          "tag_name": "v1.3.0",
          "assets": [
            { "name": "Some-Renamed-Asset-self-contained.msi", "size": 80000000,
              "browser_download_url": "https://example.com/sc.msi" }
          ]
        }
        """;

        var client = MakeClient(json, HttpStatusCode.OK);
        var result = await client.FetchLatestAsync("owner", "repo", MsiVariant.SelfContained, default);

        Assert.True(result.Ok);
        Assert.Equal("Some-Renamed-Asset-self-contained.msi", result.Info!.MsiAssetName);
    }

    [Fact]
    public async Task FetchLatest_fails_when_no_matching_asset()
    {
        var json = """
        {
          "tag_name": "v1.3.0",
          "assets": [
            { "name": "BlockRdpBruteForce-1.3.0-framework-dependent.msi", "size": 4000000,
              "browser_download_url": "https://example.com/fd.msi" }
          ]
        }
        """;

        var client = MakeClient(json, HttpStatusCode.OK);
        var result = await client.FetchLatestAsync("owner", "repo", MsiVariant.SelfContained, default);

        Assert.False(result.Ok);
        Assert.Contains("self-contained", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchLatest_propagates_http_errors()
    {
        var client = MakeClient("{}", HttpStatusCode.NotFound);
        var result = await client.FetchLatestAsync("owner", "repo", MsiVariant.SelfContained, default);

        Assert.False(result.Ok);
        Assert.Contains("404", result.Error);
    }

    [Fact]
    public async Task FetchLatest_rejects_empty_owner_or_repo()
    {
        var client = MakeClient("{}", HttpStatusCode.OK);
        var r1 = await client.FetchLatestAsync("", "repo", MsiVariant.SelfContained, default);
        var r2 = await client.FetchLatestAsync("owner", "", MsiVariant.SelfContained, default);

        Assert.False(r1.Ok);
        Assert.False(r2.Ok);
    }

    private static GitHubReleaseClient MakeClient(string body, HttpStatusCode status)
    {
        var handler = new StubHandler(body, status);
        var client = new HttpClient(handler);
        var factory = new SingleHttpClientFactory(client);
        return new GitHubReleaseClient(factory, NullLogger<GitHubReleaseClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class SingleHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleHttpClientFactory(HttpClient client) { _client = client; }
        public HttpClient CreateClient(string name) => _client;
    }
}
