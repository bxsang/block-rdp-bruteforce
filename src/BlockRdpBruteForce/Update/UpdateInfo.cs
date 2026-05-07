namespace BlockRdpBruteForce.Update;

public enum MsiVariant
{
    SelfContained,
    FrameworkDependent,
}

public sealed class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string ReleaseUrl { get; set; } = string.Empty;
    public string MsiAssetName { get; set; } = string.Empty;
    public string MsiAssetUrl { get; set; } = string.Empty;
    public long MsiAssetSize { get; set; }
}

public sealed class UpdateStateRecord
{
    public DateTime? LastCheckUtc { get; set; }
    public string? LatestVersion { get; set; }
    public string? LatestReleaseUrl { get; set; }
    public string? MsiAssetName { get; set; }
    public string? MsiAssetUrl { get; set; }
    public long MsiAssetSize { get; set; }
    public string? MsiDownloadedPath { get; set; }
    public DateTime? LastCheckErrorUtc { get; set; }
    public string? LastCheckError { get; set; }
    public DateTime? LastApplyAttemptUtc { get; set; }
    public string? LastAppliedVersion { get; set; }
    public DateTime? LastAppliedUtc { get; set; }
    public string? LastApplyError { get; set; }
}

public sealed class UpdateApplyingMarker
{
    public const string StageLaunched = "launched";
    public const string StageDownloading = "downloading";
    public const string StageInstalling = "installing";
    public const string StageDone = "done";
    public const string StageFailed = "failed";

    public string TargetVersion { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public string MsiPath { get; set; } = string.Empty;
    public bool LaunchedInUserSession { get; set; }

    // "launched" once the service spawns the updater; updater overwrites with
    // "downloading" / "installing" / "done" / "failed" as it progresses. Older
    // markers (pre-1.4) won't have this field — readers must treat null as
    // "launched" for backward compatibility.
    public string? Stage { get; set; }
    public DateTime? StageUpdatedUtc { get; set; }
    public string? LastError { get; set; }
}
