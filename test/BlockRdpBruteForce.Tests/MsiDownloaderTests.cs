using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using BlockRdpBruteForce.Updater;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class MsiDownloaderTests : IDisposable
{
    private readonly string _dir;

    public MsiDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"brbf-msidl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private static byte[] MakePayload(int sizeBytes)
    {
        // Deterministic content; size > MinExpectedSize (100_000) so download passes the sanity check.
        var buffer = new byte[sizeBytes];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 251);
        return buffer;
    }

    [Fact]
    public async Task Reports_increasing_progress_and_writes_file()
    {
        var payload = MakePayload(150_000);
        var handler = new StubHandler(payload);
        var http = new HttpClient(handler);
        var downloader = new MsiDownloader(http);

        var target = Path.Combine(_dir, "asset.msi");
        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(reports.Add);

        var result = await downloader.DownloadAsync(
            "https://example.invalid/asset.msi",
            target,
            payload.Length,
            progress,
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(payload.Length, result.Bytes);
        Assert.True(File.Exists(target));
        Assert.Equal(payload.Length, new FileInfo(target).Length);

        // Progress callbacks fire asynchronously through the Progress<T> SynchronizationContext;
        // give them a beat to drain before asserting.
        for (var i = 0; i < 20 && reports.Count < 1; i++) await Task.Delay(25);

        Assert.NotEmpty(reports);
        long lastBytes = -1;
        foreach (var r in reports)
        {
            Assert.True(r.BytesRead >= lastBytes, "bytes read should be monotonically non-decreasing");
            Assert.Equal(payload.Length, r.TotalBytes);
            lastBytes = r.BytesRead;
        }
        Assert.Equal(payload.Length, reports[^1].BytesRead);
    }

    [Fact]
    public async Task Returns_failure_for_too_small_payload()
    {
        var payload = MakePayload(1_000); // way under 100_000 sanity threshold
        var handler = new StubHandler(payload);
        var http = new HttpClient(handler);
        var downloader = new MsiDownloader(http);

        var target = Path.Combine(_dir, "asset.msi");
        var result = await downloader.DownloadAsync(
            "https://example.invalid/asset.msi", target, payload.Length, progress: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(File.Exists(target), "Failed download should not leave a file at target");
    }

    [Fact]
    public async Task Returns_failure_on_non_2xx()
    {
        var handler = new StubHandler(System.Text.Encoding.UTF8.GetBytes("not found"))
        {
            StatusCode = HttpStatusCode.NotFound,
        };
        var http = new HttpClient(handler);
        var downloader = new MsiDownloader(http);

        var target = Path.Combine(_dir, "asset.msi");
        var result = await downloader.DownloadAsync(
            "https://example.invalid/asset.msi", target, 100_000, progress: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("404", result.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public StubHandler(byte[] payload) { _payload = payload; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // Wrap in a stream that yields in chunks so progress reports fire more than once.
            var stream = new ChunkedStream(_payload, chunkSize: 8192);
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentLength = _payload.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _pos;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length) return 0;
            var take = Math.Min(Math.Min(count, _chunkSize), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
