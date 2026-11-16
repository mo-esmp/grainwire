using System.Collections.Concurrent;

namespace GrainWire.Server.Internal;

/// <summary>
/// Allows a number of events per key in a sliding one-minute window. A caller takes a slot before the work starts and
/// gives it back if the work turns out not to count, so callers that arrive together cannot all pass one check.
/// Call <see cref="Sweep"/> now and then: it forgets keys with nothing left in their window, so memory follows the
/// keys that were active in the last minute, not every key ever seen.
/// </summary>
internal sealed class PerMinuteLimiter(int limit, Func<long>? clock = null)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Func<long> _now = clock ?? (static () => Environment.TickCount64);

    /// <summary>Keys that currently have a window.</summary>
    public int KeyCount => _windows.Count;

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

        while (true)
        {
            Window window = _windows.GetOrAdd(key, static _ => new Window());
            lock (window)
            {
                if (window.Removed)
                {
                    // Swept between the lookup and the lock. A slot taken here would be lost: start over.
                    continue;
                }

                Prune(window);
                if (window.Taken.Count >= limit)
                {
                    return false;
                }

                ticket = _now();
                window.Taken.Add(ticket);
                return true;
            }
        }
    }

    /// <summary>Gives back a slot taken with <see cref="TryReserve"/>.</summary>
    public void Release(string key, long ticket)
    {
        if (!_windows.TryGetValue(key, out Window? window))
        {
            return;
        }

        lock (window)
        {
            window.Taken.Remove(ticket);
        }
    }

    /// <summary>Forgets every key whose slots have all expired or been given back.</summary>
    public void Sweep()
    {
        foreach (KeyValuePair<string, Window> entry in _windows)
        {
            lock (entry.Value)
            {
                Prune(entry.Value);
                if (entry.Value.Taken.Count == 0)
                {
                    entry.Value.Removed = true;
                    _windows.TryRemove(entry);
                }
            }
        }
    }

    private void Prune(Window window)
    {
        long cutoff = _now() - 60_000;
        window.Taken.RemoveAll(taken => taken < cutoff);
    }

    private sealed class Window
    {
        public readonly List<long> Taken = [];

        /// <summary>Set, under the window's lock, when the window has been taken out of the dictionary.</summary>
        public bool Removed;
    }
}
