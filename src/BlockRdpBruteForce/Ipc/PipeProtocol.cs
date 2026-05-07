using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockRdpBruteForce.Ipc;

public static class PipeOps
{
    public const string Status = "status";
    public const string List = "list";
    public const string Unblock = "unblock";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string ConfigGet = "config-get";
    public const string ConfigSet = "config-set";
    public const string WhitelistAdd = "whitelist-add";
    public const string WhitelistRemove = "whitelist-remove";
    public const string GeoStatus = "geo-status";
    public const string GeoRefresh = "geo-refresh";
    public const string UpdateStatus = "update-status";
    public const string UpdateCheckNow = "update-check-now";
    public const string UpdateApply = "update-apply";
}

public sealed class PipeRequest
{
    public string Op { get; set; } = string.Empty;
    public string? Ip { get; set; }
    public int? PauseMinutes { get; set; }
    public ConfigPayload? Config { get; set; }
    public string? Cidr { get; set; }
    public string? Version { get; set; }
}

public sealed class PipeResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public StatusPayload? Status { get; set; }
    public List<IpEntry>? Items { get; set; }
    public UnblockPayload? Unblock { get; set; }
    public PausePayload? Pause { get; set; }
    public ConfigPayload? ConfigEffective { get; set; }
    public ConfigSetResult? ConfigSet { get; set; }
    public GeoStatusPayload? GeoStatus { get; set; }
    public UpdateStatusPayload? UpdateStatus { get; set; }
    public UpdateApplyPayload? UpdateApply { get; set; }

    public static PipeResponse Failure(string message) => new() { Ok = false, Error = message };
}

public sealed class ConfigPayload
{
    public int? FailureThreshold { get; set; }
    public int? SlidingWindowMinutes { get; set; }
    public int? BlockDurationMinutes { get; set; }
    public List<string>? Whitelist { get; set; }
    public string? FirewallScope { get; set; }
    public bool? EvaluateNlaFallback { get; set; }
    public int? HistoryRetentionDays { get; set; }
    public bool? GeoLookupEnabled { get; set; }
    public string? IpInfoToken { get; set; }
    public int? GeoRefreshIntervalDays { get; set; }
    public bool? AutoUpdateEnabled { get; set; }
    public int? AutoUpdateCheckIntervalHours { get; set; }
}

public sealed class ConfigSetResult
{
    public ConfigPayload Effective { get; set; } = new();
    public bool RestartRequired { get; set; }
    public List<string> AppliedHot { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class StatusPayload
{
    public string ServiceName { get; set; } = "BlockRdpBruteForce";
    public int FailureThreshold { get; set; }
    public int SlidingWindowMinutes { get; set; }
    public int BlockDurationMinutes { get; set; }
    public string FirewallRuleName { get; set; } = string.Empty;
    public int BlockedIpCount { get; set; }
    public int WhitelistEntryCount { get; set; }
    public bool EvaluateNlaFallback { get; set; }
    public DateTime NowUtc { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? PausedUntilUtc { get; set; }
}

public sealed class IpEntry
{
    public string Ip { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? BlockedUntilUtc { get; set; }
    public string? CountryCode { get; set; }
    public string? Asn { get; set; }
    public string? AsName { get; set; }
}

public sealed class GeoStatusPayload
{
    public bool Enabled { get; set; }
    public bool TokenConfigured { get; set; }
    public bool DbPresent { get; set; }
    public long DbBytes { get; set; }
    public DateTime? DbModifiedUtc { get; set; }
    public DateTime? LastRefreshUtc { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
    public int IntervalDays { get; set; }
    public bool RefreshInProgress { get; set; }
}

public sealed class UpdateStatusPayload
{
    public bool AutoUpdateEnabled { get; set; }
    public int CheckIntervalHours { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string? LatestVersion { get; set; }
    public string? LatestReleaseUrl { get; set; }
    public string? MsiAssetName { get; set; }
    public bool MsiDownloaded { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public DateTime? LastCheckErrorUtc { get; set; }
    public string? LastCheckError { get; set; }
    public DateTime? LastApplyAttemptUtc { get; set; }
    public string? LastAppliedVersion { get; set; }
    public DateTime? LastAppliedUtc { get; set; }
    public string? LastApplyError { get; set; }
    public bool UpdateAvailable { get; set; }
    public string Variant { get; set; } = string.Empty;
}

public sealed class UpdateApplyPayload
{
    public bool Started { get; set; }
    public string? Message { get; set; }
}

public sealed class UnblockPayload
{
    public string Ip { get; set; } = string.Empty;
    public bool WasBlocked { get; set; }
}

public sealed class PausePayload
{
    public DateTime? PausedUntilUtc { get; set; }
}

public static class PipeProtocol
{
    public const int MaxRequestBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var line = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, line, 0, bytes.Length);
        line[^1] = (byte)'\n';
        return line;
    }

    public static T? Decode<T>(ReadOnlySpan<byte> utf8Json)
    {
        var trimmed = utf8Json;
        while (trimmed.Length > 0 &&
               (trimmed[^1] == (byte)'\n' || trimmed[^1] == (byte)'\r'))
            trimmed = trimmed[..^1];
        return JsonSerializer.Deserialize<T>(trimmed, Json);
    }
}
