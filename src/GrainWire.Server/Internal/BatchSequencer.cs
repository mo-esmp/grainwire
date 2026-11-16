using System.Collections.Concurrent;
using GrainWire.Protocol;

namespace GrainWire.Server.Internal;

/// <summary>
/// Puts the batches of each room back in the order the room sent them. Rooms send batches with one-way calls, which
/// Orleans may deliver out of order; every batch carries a number that counts up per room and gateway within an epoch.
/// A batch that never arrives is skipped after a short wait (clients then repair themselves with a sync).
/// Epochs are ranked by <see cref="DeliveryBatch.EpochOrder"/>, never by which one arrived first: a batch of an
/// older epoch is dropped even if this gateway has not seen that epoch before.
/// </summary>
internal sealed class BatchSequencer(Func<DeliveryBatch, Task> publish, TimeSpan gapTimeout)
{
    private const int MaxPending = 1024;
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.Ordinal);

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

            Drain(state);
            if (state.Pending.Count > 0 && !state.Waiting)
            {
                state.Waiting = true;
                _ = SkipGapLaterAsync(state, state.Next);
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

    /// <summary>Forgets rooms that have been quiet for a while.</summary>
    public void Prune(TimeSpan idle)
    {
        long cutoff = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        foreach (KeyValuePair<string, RoomState> entry in _rooms)
        {
            lock (entry.Value)
            {
                if (entry.Value.LastActivity < cutoff && entry.Value.Pending.Count == 0)
                {
                    _rooms.TryRemove(entry);
                }
            }
        }
    }

    private void Drain(RoomState state)
    {
        while (state.Pending.Remove(state.Next, out DeliveryBatch? batch))
        {
            state.Next++;
            state.Tail = state.Tail.ContinueWith(_ => publish(batch), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SkipGapLaterAsync(RoomState state, long waitingFor)
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
                Drain(state);
            }

            if (state.Pending.Count > 0)
            {
                state.Waiting = true;
                _ = SkipGapLaterAsync(state, state.Next);
            }
        }
    }

    private sealed class RoomState
    {
        public string? Epoch;
        public long EpochOrder;
        public long Next = 1;
        public bool Waiting;
        public long LastActivity;
        public Task Tail = Task.CompletedTask;
        public readonly SortedDictionary<long, DeliveryBatch> Pending = [];
    }
}
