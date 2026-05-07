using System.Runtime.Versioning;
using BlockRdpBruteForce.Update;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class UpdateApplyCompletionServiceTests
{
    private static UpdateApplyingMarker MakeMarker(string? stage, DateTime startedUtc) =>
        new()
        {
            TargetVersion = "1.4.0",
            StartedUtc = startedUtc,
            MsiPath = "C:\\fake.msi",
            LaunchedInUserSession = true,
            Stage = stage,
        };

    [Fact]
    public void Launched_marker_becomes_stale_after_15_minutes()
    {
        var started = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        var marker = MakeMarker(UpdateApplyingMarker.StageLaunched, started);

        Assert.False(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(14)));
        Assert.True(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(16)));
    }

    [Fact]
    public void Downloading_marker_uses_one_hour_window()
    {
        var started = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        var marker = MakeMarker(UpdateApplyingMarker.StageDownloading, started);

        Assert.False(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(30)));
        Assert.False(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(59)));
        Assert.True(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(61)));
    }

    [Fact]
    public void Installing_marker_uses_one_hour_window()
    {
        var started = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        var marker = MakeMarker(UpdateApplyingMarker.StageInstalling, started);

        Assert.False(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(30)));
        Assert.True(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(61)));
    }

    [Fact]
    public void Null_stage_is_treated_as_launched()
    {
        var started = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        var marker = MakeMarker(stage: null, started);

        Assert.False(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(14)));
        Assert.True(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(16)));
    }

    [Fact]
    public void Empty_stage_is_treated_as_launched()
    {
        var started = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        var marker = MakeMarker(stage: string.Empty, started);

        Assert.True(UpdateApplyCompletionService.IsMarkerStale(marker, started.AddMinutes(16)));
    }
}
