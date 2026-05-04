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
                BlockDurationMinutes = 1440,
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
}
