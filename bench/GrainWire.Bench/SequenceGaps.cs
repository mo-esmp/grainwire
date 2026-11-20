namespace GrainWire.Bench;

/// <summary>
/// Counts the events one client never saw although later ones arrived: gaps in each room's per-player sequence numbers.
/// A gap means the room sent the event and it was lost on the way (the gateway skips a batch that comes more than 250 ms
/// late, and publishes at QoS 0); a real client sees the same gap and catches up with a sync. Not thread-safe: MQTTnet
/// hands one client its messages one at a time.
/// </summary>
public sealed class SequenceGaps
{
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

    /// <summary>All gaps counted so far.</summary>
    public long Total { get; private set; }

    /// <summary>Notes an event of <paramref name="room"/> numbered <paramref name="seq"/>; returns the numbers it skipped.</summary>
    public long Observe(string room, long seq)
    {
        if (!_last.TryGetValue(room, out long last))
        {
            _last[room] = seq;
            return 0;
        }

        if (seq <= last)
        {
            return 0;
        }

        _last[room] = seq;
        long skipped = seq - last - 1;
        Total += skipped;
        return skipped;
    }
}
