using System.Collections.Concurrent;
using System.Net;
using BlockRdpBruteForce.Detection;

namespace BlockRdpBruteForce.Tests;

public class FailureTrackerTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.9");

    [Fact]
    public void Returns_False_Below_Threshold()
    {
        var tracker = new FailureTracker(threshold: 5, window: TimeSpan.FromMinutes(10));
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 4; i++)
            Assert.False(tracker.Record(Ip, t.AddSeconds(i)));
    }

    [Fact]
    public void Returns_True_At_Threshold()
    {
        var tracker = new FailureTracker(threshold: 5, window: TimeSpan.FromMinutes(10));
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        bool last = false;
        for (var i = 0; i < 5; i++)
            last = tracker.Record(Ip, t.AddSeconds(i));
        Assert.True(last);
    }

    [Fact]
    public void Evicts_Records_Older_Than_Window()
    {
        var tracker = new FailureTracker(threshold: 5, window: TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 4; i++)
            tracker.Record(Ip, t0.AddSeconds(i));

        var later = t0.AddMinutes(11);
        Assert.False(tracker.Record(Ip, later));
        Assert.Equal(1, tracker.Count(Ip, later));
    }

    [Fact]
    public void Window_At_Boundary_Is_Inclusive_Of_Recent_Edge()
    {
        var tracker = new FailureTracker(threshold: 3, window: TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        tracker.Record(Ip, t0);
        tracker.Record(Ip, t0.AddMinutes(5));

        var atEdge = t0.AddMinutes(10);
        Assert.True(tracker.Record(Ip, atEdge));
        Assert.Equal(3, tracker.Count(Ip, atEdge));
    }

    [Fact]
    public void Different_Ips_Track_Separately()
    {
        var tracker = new FailureTracker(threshold: 3, window: TimeSpan.FromMinutes(10));
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var a = IPAddress.Parse("203.0.113.1");
        var b = IPAddress.Parse("203.0.113.2");
        tracker.Record(a, t);
        tracker.Record(a, t.AddSeconds(1));
        tracker.Record(b, t);

        Assert.Equal(2, tracker.Count(a, t.AddSeconds(2)));
        Assert.Equal(1, tracker.Count(b, t.AddSeconds(2)));
    }

    [Fact]
    public void Reset_Clears_Single_Ip()
    {
        var tracker = new FailureTracker(threshold: 3, window: TimeSpan.FromMinutes(10));
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        tracker.Record(Ip, t);
        tracker.Record(Ip, t.AddSeconds(1));
        tracker.Reset(Ip);
        Assert.Equal(0, tracker.Count(Ip, t.AddSeconds(2)));
    }

    [Fact]
    public void Concurrent_Record_Threshold_Eventually_Reached()
    {
        var tracker = new FailureTracker(threshold: 50, window: TimeSpan.FromMinutes(10));
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var trueCount = 0;

        Parallel.For(0, 200, i =>
        {
            if (tracker.Record(Ip, t.AddMilliseconds(i)))
                Interlocked.Increment(ref trueCount);
        });

        Assert.Equal(200, tracker.Count(Ip, t.AddSeconds(1)));
        Assert.Equal(151, trueCount);
    }

    [Fact]
    public void Concurrent_Record_Across_Many_Ips_Is_Race_Free()
    {
        var tracker = new FailureTracker(threshold: 5, window: TimeSpan.FromMinutes(10));
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ips = Enumerable.Range(0, 64).Select(i => IPAddress.Parse($"203.0.113.{i}")).ToArray();
        var per = new ConcurrentDictionary<IPAddress, int>();

        Parallel.For(0, 64 * 10, idx =>
        {
            var ip = ips[idx % 64];
            tracker.Record(ip, t.AddMilliseconds(idx));
            per.AddOrUpdate(ip, 1, (_, v) => v + 1);
        });

        foreach (var ip in ips)
            Assert.Equal(per[ip], tracker.Count(ip, t.AddSeconds(1)));
    }

    [Fact]
    public void Constructor_Rejects_Invalid_Args()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FailureTracker(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FailureTracker(5, TimeSpan.Zero));
    }
}
