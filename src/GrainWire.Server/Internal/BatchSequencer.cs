using System.Collections.Concurrent;
using GrainWire.Protocol;

namespace GrainWire.Server.Internal;

/// <summary>
/// Puts the batches of each room back in the order the room sent them, and publishes them one at a time.
/// Rooms send batches with one-way calls, which Orleans may deliver out of order; every batch carries a number that
/// counts up per room and gateway within an epoch. A batch that never arrives is skipped after a short wait.
/// Epochs are ranked by <see cref="DeliveryBatch.EpochOrder"/>, never by which one arrived first: a batch of an
/// older epoch is dropped even if this gateway has not seen that epoch before.
/// <para>
/// Both queues of a room are bounded: the batches waiting for a missing one, and the batches waiting to be published.
/// When publishing cannot keep up, new batches are dropped instead of piling up in memory. Clients see the missing
/// events as a gap and catch up with a sync, exactly as for a batch lost on the way; a lost <c>$closed</c> notice
/// is repaired by the next lease renewal.
/// </para>
/// </summary>
internal sealed class BatchSequencer(Func<DeliveryBatch, Task> publish, TimeSpan gapTimeout, int maxBacklog = BatchSequencer.DefaultMaxBacklog, Action<string>? onOverflow = null)
{
    /// <summary>Batches of one room that may wait to be published.</summary>
    public const int DefaultMaxBacklog = 1024;

    private const int MaxPending = 1024;
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.Ordinal);
    private long _dropped;

    /// <summary>Batches dropped so far because publishing could not keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Accept(DeliveryBatch batch)
    {
        RoomState state = _rooms.GetOrAdd(batch.Room, static _ => new RoomState());
        lock (state)
        {
            state.LastActivity = Environment.TickCount64;
            if (batch.Epoch != state.Epoch)
            {
                // Only a higher-ranked epoch replaces the current one. A late batch of an older epoch is stale, whether
                // or not that epoch was seen here before; publishing it would put old events after the recovered stream.
                if (state.Epoch is not null && batch.EpochOrder <= state.EpochOrder)
                {
                    return;
                }

                state.Epoch = batch.Epoch;
                state.EpochOrder = batch.EpochOrder;
                state.Next = 1;
                state.Pending.Clear();
            }
            else if (batch.EpochOrder > state.EpochOrder)
            {
                // The room raised its own rank (see RoomGrain.TouchGateway).
                state.EpochOrder = batch.EpochOrder;
            }

            if (batch.Number < state.Next)
            {
                return;
            }

            state.Pending[batch.Number] = batch;
            if (state.Pending.Count > MaxPending)
            {
                state.Next = state.Pending.Keys.First();
            }

            Drain(batch.Room, state);
            if (state.Pending.Count > 0 && !state.Waiting)
            {
                state.Waiting = true;
                _ = SkipGapLaterAsync(batch.Room, state, state.Next);
            }
        }
    }

    /// <summary>The newest epoch seen for a room, sent to the room whenever this gateway attaches connections to it.</summary>
    public KnownEpoch Known(string room)
    {
        if (!_rooms.TryGetValue(room, out RoomState? state))
        {
            return default;
        }

        lock (state)
        {
            return new KnownEpoch(state.Epoch, state.EpochOrder);
        }
    }

    /// <summary>Batches of a room that are in order and waiting to be published.</summary>
    public int Backlog(string room)
    {
        if (!_rooms.TryGetValue(room, out RoomState? state))
        {
            return 0;
        }

        lock (state)
        {
            return state.Ready.Count;
        }
    }

    /// <summary>Forgets rooms that have been quiet for a while.</summary>
    public void Prune(TimeSpan idle)
    {
        long cutoff = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        foreach (KeyValuePair<string, RoomState> entry in _rooms)
        {
            lock (entry.Value)
            {
                if (entry.Value.LastActivity < cutoff && entry.Value.Pending.Count == 0 && entry.Value.Ready.Count == 0 && !entry.Value.Publishing)
                {
                    _rooms.TryRemove(entry);
                }
            }
        }
    }

    /// <summary>Moves the batches that are next in line to the publish queue. Call with the room's lock held.</summary>
    private void Drain(string room, RoomState state)
    {
        while (state.Pending.Remove(state.Next, out DeliveryBatch? batch))
        {
            state.Next++;
            if (state.Ready.Count >= maxBacklog)
            {
                // Publishing cannot keep up. Holding on to the batch would only move the problem into memory.
                Interlocked.Increment(ref _dropped);
                if (!state.Overflowing)
                {
                    // Report once per episode, not once per batch.
                    state.Overflowing = true;
                    onOverflow?.Invoke(room);
                }

                continue;
            }

            state.Ready.Enqueue(batch);
        }

        if (state.Ready.Count > 0 && !state.Publishing)
        {
            state.Publishing = true;
            _ = Task.Run(() => PublishAsync(state));
        }
    }

    /// <summary>One at a time per room, so the order is kept.</summary>
    private async Task PublishAsync(RoomState state)
    {
        while (true)
        {
            DeliveryBatch? batch;
            lock (state)
            {
                if (!state.Ready.TryDequeue(out batch))
                {
                    state.Publishing = false;
                    state.Overflowing = false;
                    return;
                }
            }

            try
            {
                await publish(batch);
            }
            catch (Exception)
            {
                // The publisher reports its own failures. For clients it is a lost batch: they catch up with a sync.
            }
        }
    }

    private async Task SkipGapLaterAsync(string room, RoomState state, long waitingFor)
    {
        await Task.Delay(gapTimeout);
        lock (state)
        {
            state.Waiting = false;
            if (state.Pending.Count == 0)
            {
                return;
            }

            if (state.Next == waitingFor)
            {
                // Still stuck on the same missing batch: give up on it.
                state.Next = state.Pending.Keys.First();
                Drain(room, state);
            }

            if (state.Pending.Count > 0)
            {
                state.Waiting = true;
                _ = SkipGapLaterAsync(room, state, state.Next);
            }
        }
    }

    private sealed class RoomState
    {
        public string? Epoch;
        public long EpochOrder;
        public long Next = 1;
        public bool Waiting;
        public bool Publishing;
        public bool Overflowing;
        public long LastActivity;
        public readonly SortedDictionary<long, DeliveryBatch> Pending = [];
        public readonly Queue<DeliveryBatch> Ready = new();
    }
}
