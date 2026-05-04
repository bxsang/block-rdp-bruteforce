using System.Collections.Concurrent;
using System.Net;

namespace BlockRdpBruteForce.Detection;

public sealed class FailureTracker
{
    private readonly ConcurrentDictionary<IPAddress, Queue<DateTime>> _windows = new();
    private readonly int _threshold;
    private readonly TimeSpan _window;

    public FailureTracker(int threshold, TimeSpan window)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _threshold = threshold;
        _window = window;
    }

    public int Threshold => _threshold;
    public TimeSpan Window => _window;

    public bool Record(IPAddress ip, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var queue = _windows.GetOrAdd(ip, static _ => new Queue<DateTime>());
        lock (queue)
        {
            queue.Enqueue(utcNow);
            EvictExpired(queue, utcNow);
            return queue.Count >= _threshold;
        }
    }

    public int Count(IPAddress ip, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (!_windows.TryGetValue(ip, out var queue)) return 0;
        lock (queue)
        {
            EvictExpired(queue, utcNow);
            return queue.Count;
        }
    }

    public void Reset(IPAddress ip) => _windows.TryRemove(ip, out _);

    public void Clear() => _windows.Clear();

    private void EvictExpired(Queue<DateTime> queue, DateTime utcNow)
    {
        var cutoff = utcNow - _window;
        while (queue.Count > 0 && queue.Peek() < cutoff)
            queue.Dequeue();
    }
}
