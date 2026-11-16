using System.Collections.Concurrent;

namespace GrainWire.Server.Internal;

/// <summary>
/// Disconnects that a room could not be told about. They are tried again until the room has heard them, otherwise the
/// room would show the player as online forever.
/// <para>
/// Retries run on their own, never as part of a lease renewal: a room that is slow or cannot be reached must not delay
/// the renewals of healthy rooms. Each attempt has a time budget, a few run at the same time, and a round takes a
/// limited number of them, so a large backlog is worked off steadily without flooding the cluster.
/// </para>
/// </summary>
internal sealed class DetachRetryQueue(Func<RoomAddress, ConnectionId, Task> detach, TimeSpan budget, int concurrency = 8, int perRound = 256)
{
    private readonly ConcurrentQueue<(RoomAddress Room, ConnectionId Connection)> _pending = new();
    private readonly object _gate = new();
    private Task _round = Task.CompletedTask;

    public int Count => _pending.Count;

    public void Add(RoomAddress room, ConnectionId connection) => _pending.Enqueue((room, connection));

    /// <summary>
    /// Starts a round of retries unless one is still running or nothing is waiting. Returns at once; the returned
    /// task is the round, for callers that want to wait for it (tests).
    /// </summary>
    public Task Start(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_round.IsCompleted && !_pending.IsEmpty)
            {
                _round = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
            }

            return _round;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        List<(RoomAddress Room, ConnectionId Connection)> round = [];
        while (round.Count < perRound && _pending.TryDequeue(out (RoomAddress Room, ConnectionId Connection) pending))
        {
            round.Add(pending);
        }

        // Not cancelled as a whole: every item either reaches its room or goes back into the queue.
        await Parallel.ForEachAsync(round, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency) }, async (item, _) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await detach(item.Room, item.Connection).WaitAsync(budget, cancellationToken);
            }
            catch (Exception)
            {
                // Still not told (failed, too slow, or shutting down): the next round tries again.
                _pending.Enqueue(item);
            }
        });
    }
}
