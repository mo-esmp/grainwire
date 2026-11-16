using System.Collections.Concurrent;
using GrainWire.Protocol;

namespace GrainWire.Server.Internal;

/// <summary>
/// Puts the batches of each room back in the order the room sent them. Rooms send batches with one-way calls, which
/// Orleans may deliver out of order; every batch carries a number that counts up per room and gateway within an epoch.
/// A batch that never arrives is skipped after a short wait (clients then repair themselves with a sync).
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
                // A batch from an epoch that already ended is stale; anything else starts a new epoch.
                if (state.PastEpochs.Contains(batch.Epoch))
                {
                    return;
                }

                if (state.Epoch is not null)
                {
                    state.PastEpochs.Enqueue(state.Epoch);
                    if (state.PastEpochs.Count > 8)
                    {
                        state.PastEpochs.Dequeue();
                    }
                }

                state.Epoch = batch.Epoch;
                state.Next = 1;
                state.Pending.Clear();
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
        public long Next = 1;
        public bool Waiting;
        public long LastActivity;
        public Task Tail = Task.CompletedTask;
        public readonly SortedDictionary<long, DeliveryBatch> Pending = [];
        public readonly Queue<string> PastEpochs = new();
    }
}
