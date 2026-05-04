using System.Net;
using System.Text.Json;
using BlockRdpBruteForce.State;

namespace BlockRdpBruteForce.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _dir;

    public StateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "blockrdp-state-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string FilePath(string file = "state.json") => Path.Combine(_dir, file);

    [Fact]
    public void Load_Of_Missing_File_Yields_Empty_Store()
    {
        var store = new StateStore(FilePath());
        store.Load();
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Upsert_Increments_Count_And_Tracks_Times()
    {
        var store = new StateStore(FilePath());
        var ip = IPAddress.Parse("203.0.113.1");
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var first = store.Upsert(ip, t, TimeSpan.FromMinutes(60));
        var second = store.Upsert(ip, t.AddMinutes(2), TimeSpan.FromMinutes(60));

        Assert.Equal(1, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(t, second.FirstSeenUtc);
        Assert.Equal(t.AddMinutes(2), second.LastSeenUtc);
        Assert.Equal(t.AddMinutes(2).AddMinutes(60), second.BlockedUntilUtc);
    }

    [Fact]
    public void Upsert_With_Null_Duration_Means_Permanent()
    {
        var store = new StateStore(FilePath());
        var rec = store.Upsert(IPAddress.Parse("203.0.113.1"), DateTime.UtcNow, null);
        Assert.Null(rec.BlockedUntilUtc);
    }

    [Fact]
    public void Save_Then_Load_Round_Trips()
    {
        var path = FilePath();
        var store = new StateStore(path);
        var ip = IPAddress.Parse("203.0.113.1");
        var ip6 = IPAddress.Parse("2001:db8::1");
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        store.Upsert(ip, t, TimeSpan.FromHours(24));
        store.Upsert(ip6, t.AddSeconds(1), null);
        store.Save();

        Assert.True(File.Exists(path));

        var reloaded = new StateStore(path);
        reloaded.Load();
        Assert.Equal(2, reloaded.Snapshot().Count);
        Assert.NotNull(reloaded.TryGet(ip));
        Assert.NotNull(reloaded.TryGet(ip6));
        Assert.Null(reloaded.TryGet(ip6)!.BlockedUntilUtc);
    }

    [Fact]
    public void Save_Is_Atomic_Via_Replace()
    {
        var path = FilePath();
        var store = new StateStore(path);
        store.Upsert(IPAddress.Parse("203.0.113.1"), DateTime.UtcNow, TimeSpan.FromMinutes(10));
        store.Save();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_Creates_Missing_Parent_Directory()
    {
        var nested = System.IO.Path.Combine(_dir, "a", "b", "state.json");
        var store = new StateStore(nested);
        store.Upsert(IPAddress.Parse("203.0.113.1"), DateTime.UtcNow, null);
        store.Save();
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Load_Tolerates_Corrupt_Json()
    {
        var path = FilePath();
        File.WriteAllText(path, "{not valid json");
        var store = new StateStore(path);
        store.Load();
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Load_Skips_Records_With_Invalid_Ip()
    {
        var path = FilePath();
        var json = """
        {
          "Ips": [
            { "Ip": "203.0.113.1", "Count": 1, "FirstSeenUtc": "2025-01-01T00:00:00Z", "LastSeenUtc": "2025-01-01T00:00:00Z" },
            { "Ip": "not-an-ip",   "Count": 1, "FirstSeenUtc": "2025-01-01T00:00:00Z", "LastSeenUtc": "2025-01-01T00:00:00Z" }
          ]
        }
        """;
        File.WriteAllText(path, json);

        var store = new StateStore(path);
        store.Load();
        Assert.Single(store.Snapshot());
        Assert.NotNull(store.TryGet(IPAddress.Parse("203.0.113.1")));
    }

    [Fact]
    public void Active_And_Expired_Partition_By_Time()
    {
        var store = new StateStore(FilePath());
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var a = IPAddress.Parse("203.0.113.1");
        var b = IPAddress.Parse("203.0.113.2");
        var c = IPAddress.Parse("203.0.113.3");
        store.Upsert(a, t, TimeSpan.FromMinutes(10));
        store.Upsert(b, t, null);
        store.Upsert(c, t.AddMinutes(-30), TimeSpan.FromMinutes(5));

        var active = store.ActiveBlockedIps(t.AddMinutes(1));
        var expired = store.ExpiredIps(t.AddMinutes(1));

        Assert.Contains(a, active);
        Assert.Contains(b, active);
        Assert.DoesNotContain(c, active);
        Assert.Contains(c, expired);
        Assert.DoesNotContain(a, expired);
    }

    [Fact]
    public void Remove_Returns_True_Only_When_Present()
    {
        var store = new StateStore(FilePath());
        var ip = IPAddress.Parse("203.0.113.1");
        store.Upsert(ip, DateTime.UtcNow, TimeSpan.FromMinutes(10));
        Assert.True(store.Remove(ip));
        Assert.False(store.Remove(ip));
    }

    [Fact]
    public void Snapshot_Is_A_Defensive_Copy()
    {
        var store = new StateStore(FilePath());
        var ip = IPAddress.Parse("203.0.113.1");
        store.Upsert(ip, DateTime.UtcNow, TimeSpan.FromMinutes(10));

        var snap = store.Snapshot();
        snap[0].Count = 9999;

        Assert.NotEqual(9999, store.TryGet(ip)!.Count);
    }

    [Fact]
    public void Constructor_Expands_Environment_Variables()
    {
        var raw = "%TEMP%\\blockrdp-test-" + Guid.NewGuid().ToString("N") + ".json";
        var store = new StateStore(raw);
        Assert.DoesNotContain("%", store.ResolvedPath);
    }
}
