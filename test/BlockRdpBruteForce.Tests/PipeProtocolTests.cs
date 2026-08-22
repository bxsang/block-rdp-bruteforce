using System.Text;
using BlockRdpBruteForce.Ipc;

namespace BlockRdpBruteForce.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void Encode_terminates_with_newline()
    {
        var bytes = PipeProtocol.Encode(new PipeRequest { Op = "status" });
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public void Encode_uses_camel_case_property_names()
    {
        var bytes = PipeProtocol.Encode(new PipeRequest { Op = "unblock", Ip = "1.2.3.4", PauseMinutes = 30 });
        var json = Encoding.UTF8.GetString(bytes).TrimEnd('\n');
        Assert.Contains("\"op\":", json);
        Assert.Contains("\"ip\":", json);
        Assert.Contains("\"pauseMinutes\":", json);
    }

    [Fact]
    public void Encode_omits_null_properties()
    {
        var bytes = PipeProtocol.Encode(new PipeRequest { Op = "list" });
        var json = Encoding.UTF8.GetString(bytes).TrimEnd('\n');
        Assert.DoesNotContain("\"ip\":", json);
        Assert.DoesNotContain("\"pauseMinutes\":", json);
    }

    [Fact]
    public void Decode_handles_trailing_newline()
    {
        var bytes = PipeProtocol.Encode(new PipeRequest { Op = "status" });
        var decoded = PipeProtocol.Decode<PipeRequest>(bytes);
        Assert.NotNull(decoded);
        Assert.Equal("status", decoded!.Op);
    }

    [Fact]
    public void Decode_handles_crlf_terminator()
    {
        var json = "{\"op\":\"list\"}\r\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        var decoded = PipeProtocol.Decode<PipeRequest>(bytes);
        Assert.Equal("list", decoded!.Op);
    }

    [Fact]
    public void RoundTrip_status_response_preserves_all_fields()
    {
        var original = new PipeResponse
        {
            Ok = true,
            Status = new StatusPayload
            {
                ServiceName = "BlockRdpBruteForce",
                FailureThreshold = 5,
                SlidingWindowMinutes = 10,
                BlockDurationMinutes = new List<int> { 1440 },
                FirewallRuleName = "BlockRDPBruteForce",
                BlockedIpCount = 3,
                WhitelistEntryCount = 2,
                EvaluateNlaFallback = true,
                NowUtc = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc),
                StartedUtc = new DateTime(2026, 5, 4, 11, 30, 0, DateTimeKind.Utc),
                PausedUntilUtc = new DateTime(2026, 5, 4, 13, 0, 0, DateTimeKind.Utc),
            },
        };

        var bytes = PipeProtocol.Encode(original);
        var round = PipeProtocol.Decode<PipeResponse>(bytes);

        Assert.NotNull(round);
        Assert.True(round!.Ok);
        Assert.NotNull(round.Status);
        Assert.Equal(original.Status!.FailureThreshold, round.Status!.FailureThreshold);
        Assert.Equal(original.Status.SlidingWindowMinutes, round.Status.SlidingWindowMinutes);
        Assert.Equal(original.Status.BlockedIpCount, round.Status.BlockedIpCount);
        Assert.Equal(original.Status.NowUtc, round.Status.NowUtc);
        Assert.Equal(original.Status.PausedUntilUtc, round.Status.PausedUntilUtc);
    }

    [Fact]
    public void RoundTrip_list_response_preserves_entries()
    {
        var original = new PipeResponse
        {
            Ok = true,
            Items = new List<IpEntry>
            {
                new() { Ip = "1.2.3.4", Count = 7,
                    FirstSeenUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    LastSeenUtc = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                    BlockedUntilUtc = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc) },
                new() { Ip = "::1", Count = 2,
                    FirstSeenUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    LastSeenUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    BlockedUntilUtc = null },
            },
        };

        var bytes = PipeProtocol.Encode(original);
        var round = PipeProtocol.Decode<PipeResponse>(bytes);

        Assert.NotNull(round?.Items);
        Assert.Equal(2, round!.Items!.Count);
        Assert.Equal("1.2.3.4", round.Items[0].Ip);
        Assert.Equal(7, round.Items[0].Count);
        Assert.Null(round.Items[1].BlockedUntilUtc);
    }

    [Fact]
    public void Failure_response_serializes_error()
    {
        var original = PipeResponse.Failure("administrator required");
        var bytes = PipeProtocol.Encode(original);
        var round = PipeProtocol.Decode<PipeResponse>(bytes);

        Assert.NotNull(round);
        Assert.False(round!.Ok);
        Assert.Equal("administrator required", round.Error);
    }

    [Fact]
    public void RoundTrip_config_payload_preserves_all_fields()
    {
        var original = new ConfigPayload
        {
            FailureThreshold = 7,
            SlidingWindowMinutes = 15,
            BlockDurationMinutes = new List<int> { 60, 240, 1440, 0 },
            Whitelist = new List<string> { "127.0.0.1", "10.0.0.0/8" },
            FirewallScope = "RdpOnly",
            EvaluateNlaFallback = false,
        };

        var bytes = PipeProtocol.Encode(original);
        var round = PipeProtocol.Decode<ConfigPayload>(bytes);

        Assert.NotNull(round);
        Assert.Equal(7, round!.FailureThreshold);
        Assert.Equal(15, round.SlidingWindowMinutes);
        Assert.Equal(new List<int> { 60, 240, 1440, 0 }, round.BlockDurationMinutes);
        Assert.Equal("RdpOnly", round.FirewallScope);
        Assert.False(round.EvaluateNlaFallback);
        Assert.Equal(new[] { "127.0.0.1", "10.0.0.0/8" }, round.Whitelist);
    }

    [Fact]
    public void Partial_config_payload_omits_null_fields_in_json()
    {
        var bytes = PipeProtocol.Encode(new ConfigPayload { FailureThreshold = 5 });
        var json = Encoding.UTF8.GetString(bytes).TrimEnd('\n');

        Assert.Contains("\"failureThreshold\":", json);
        Assert.DoesNotContain("\"slidingWindowMinutes\":", json);
        Assert.DoesNotContain("\"whitelist\":", json);
        Assert.DoesNotContain("\"firewallScope\":", json);
    }

    [Fact]
    public void RoundTrip_config_set_result()
    {
        var original = new ConfigSetResult
        {
            Effective = new ConfigPayload { FailureThreshold = 7, Whitelist = new() { "10.0.0.0/8" } },
            RestartRequired = true,
            AppliedHot = new List<string> { "whitelist" },
        };

        var bytes = PipeProtocol.Encode(original);
        var round = PipeProtocol.Decode<ConfigSetResult>(bytes);

        Assert.NotNull(round);
        Assert.True(round!.RestartRequired);
        Assert.Single(round.AppliedHot);
        Assert.Equal("whitelist", round.AppliedHot[0]);
        Assert.Equal(7, round.Effective.FailureThreshold);
    }

    [Fact]
    public void RoundTrip_pipe_request_with_config_and_cidr()
    {
        var original = new PipeRequest
        {
            Op = PipeOps.ConfigSet,
            Config = new ConfigPayload { FailureThreshold = 9 },
            Cidr = "192.168.1.0/24",
        };

        var bytes = PipeProtocol.Encode(original);
        var round = PipeProtocol.Decode<PipeRequest>(bytes);

        Assert.NotNull(round);
        Assert.Equal(PipeOps.ConfigSet, round!.Op);
        Assert.Equal(9, round.Config!.FailureThreshold);
        Assert.Equal("192.168.1.0/24", round.Cidr);
    }

    [Fact]
    public async Task ReadLineAsync_returns_full_frame_across_chunk_boundaries()
    {
        var payload = new string('x', 3000); // spans several 512-byte reads
        var stream = new ChunkedStream(
            Encoding.UTF8.GetBytes(payload + "\ntrailing-after-frame"));

        var frame = await PipeProtocol.ReadLineAsync(stream, CancellationToken.None);

        Assert.Equal(payload, Encoding.UTF8.GetString(frame!));
    }

    [Fact]
    public async Task ReadLineAsync_returns_null_on_clean_eof()
    {
        var frame = await PipeProtocol.ReadLineAsync(
            new ChunkedStream(Array.Empty<byte>()), CancellationToken.None);

        Assert.Null(frame);
    }

    [Fact]
    public async Task ReadLineAsync_throws_when_frame_exceeds_limit()
    {
        var oversized = new byte[PipeProtocol.MaxResponseBytes + 1];
        Array.Fill(oversized, (byte)'a');

        await Assert.ThrowsAsync<IOException>(
            () => PipeProtocol.ReadLineAsync(new ChunkedStream(oversized), CancellationToken.None));
    }

    /// <summary>Delivers at most 512 bytes per Read so frames span multiple reads.</summary>
    private sealed class ChunkedStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length - _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) return 0;
            var n = Math.Min(Math.Min(count, 512), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
