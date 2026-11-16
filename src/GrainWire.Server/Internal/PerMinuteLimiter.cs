using System.Collections.Concurrent;

namespace GrainWire.Server.Internal;

/// <summary>Counts events per key in a sliding one-minute window.</summary>
internal sealed class PerMinuteLimiter(int limit)
{
    private readonly ConcurrentDictionary<string, Queue<long>> _windows = new(StringComparer.Ordinal);

    public bool HasRoom(string key)
    {
        if (!_windows.TryGetValue(key, out Queue<long>? window))
        {
            return limit > 0;
        }

        lock (window)
        {
            Prune(window);
            return window.Count < limit;
        }
    }

    public void Record(string key)
    {
        Queue<long> window = _windows.GetOrAdd(key, static _ => new Queue<long>());
        lock (window)
        {
            Prune(window);
            window.Enqueue(Environment.TickCount64);
        }
    }

    private static void Prune(Queue<long> window)
    {
        long cutoff = Environment.TickCount64 - 60_000;
        while (window.Count > 0 && window.Peek() < cutoff)
        {
            window.Dequeue();
        }
    }
}
