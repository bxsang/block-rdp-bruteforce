using System.Runtime.Versioning;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BlockRdpBruteForce.Tests;

[SupportedOSPlatform("windows")]
public sealed class UpdateStateStoreTests : IDisposable
{
    private readonly string _dir;

    public UpdateStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"brbf-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private UpdateStateStore Make() =>
        new(Options.Create(new AppOptions { UpdateDataPath = _dir }),
            NullLogger<UpdateStateStore>.Instance);

    [Fact]
    public void Empty_directory_yields_default_record()
    {
        var store = Make();
        var rec = store.Get();
        Assert.Null(rec.LatestVersion);
        Assert.Null(rec.LastCheckUtc);
    }

    [Fact]
    public void Update_persists_round_trip()
    {
        var store1 = Make();
        store1.Update(s =>
        {
            s.LatestVersion = "1.3.0";
            s.LastCheckUtc = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc);
            s.MsiAssetName = "BlockRdpBruteForce-1.3.0-self-contained.msi";
        });

        var store2 = Make();
        var rec = store2.Get();
        Assert.Equal("1.3.0", rec.LatestVersion);
        Assert.Equal("BlockRdpBruteForce-1.3.0-self-contained.msi", rec.MsiAssetName);
        Assert.Equal(new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc), rec.LastCheckUtc);
    }

    [Fact]
    public void Marker_round_trip()
    {
        var store = Make();
        Assert.Null(store.ReadMarker());

        store.WriteMarker(new UpdateApplyingMarker
        {
            TargetVersion = "1.3.0",
            StartedUtc = DateTime.UtcNow,
            MsiPath = "C:\\foo.msi",
            LaunchedInUserSession = true,
        });

        var marker = store.ReadMarker();
        Assert.NotNull(marker);
        Assert.Equal("1.3.0", marker!.TargetVersion);
        Assert.True(marker.LaunchedInUserSession);

        store.DeleteMarker();
        Assert.Null(store.ReadMarker());
    }

    [Fact]
    public void PruneOldMsis_keeps_named_file()
    {
        File.WriteAllText(Path.Combine(_dir, "BlockRdpBruteForce-1.2.0-self-contained.msi"), "old");
        File.WriteAllText(Path.Combine(_dir, "BlockRdpBruteForce-1.3.0-self-contained.msi"), "new");

        var store = Make();
        store.PruneOldMsis("BlockRdpBruteForce-1.3.0-self-contained.msi");

        Assert.False(File.Exists(Path.Combine(_dir, "BlockRdpBruteForce-1.2.0-self-contained.msi")));
        Assert.True(File.Exists(Path.Combine(_dir, "BlockRdpBruteForce-1.3.0-self-contained.msi")));
    }
}
