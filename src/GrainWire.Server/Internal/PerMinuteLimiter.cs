using System.Collections.Concurrent;

namespace GrainWire.Server.Internal;

/// <summary>
/// Allows a number of events per key in a sliding one-minute window. A caller takes a slot before the work starts and
/// gives it back if the work turns out not to count, so callers that arrive together cannot all pass one check.
/// </summary>
internal sealed class PerMinuteLimiter(int limit)
{
    private readonly ConcurrentDictionary<string, List<long>> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes one of the key's slots if one is free. Checking and taking are a single step.
    /// Pass the ticket to <see cref="Release"/> to give the slot back; otherwise it frees itself after a minute.
    /// </summary>
    public bool TryReserve(string key, out long ticket)
    {
        ticket = 0;
        if (limit <= 0)
        {
            return false;
        }

        List<long> window = _windows.GetOrAdd(key, static _ => []);
        lock (window)
        {
            Prune(window);
            if (window.Count >= limit)
            {
                return false;
            }

            ticket = Environment.TickCount64;
            window.Add(ticket);
            return true;
        }
    }

    /// <summary>Gives back a slot taken with <see cref="TryReserve"/>.</summary>
    public void Release(string key, long ticket)
    {
        if (!_windows.TryGetValue(key, out List<long>? window))
        {
            return;
        }

        lock (window)
        {
            window.Remove(ticket);
        }
    }

    private static void Prune(List<long> window)
    {
        long cutoff = Environment.TickCount64 - 60_000;
        window.RemoveAll(taken => taken < cutoff);
    }
}
