namespace GrainWire.Client.Protocol;

/// <summary>
/// Turns what arrives from the network (possibly late, twice, out of order, or not at all) into what the app may see:
/// a snapshot, then that player's events in order, each exactly once. It does no I/O and owns no timers: callers pass
/// in the current time and act on the returned actions. That keeps it testable against any network behaviour.
/// </summary>
/// <remarks>
/// Times are milliseconds on any clock that never goes backwards, such as <see cref="Environment.TickCount64"/>.
/// An engine is not thread-safe: one room has one engine, used by one caller at a time.
/// </remarks>
public sealed class OrderingEngine
{
    private const int MaxRetired = 16;
    private readonly long _gapWait;
    private readonly long _syncInterval;
    private readonly long _syncRetry;
    private readonly int _maxBuffered;
    private readonly List<EventEnvelope> _buffer = [];
    private readonly List<(string Incarnation, string Epoch)> _retired = [];
    private SnapshotEnvelope? _refused;
    private long _snapshotsReceived;
    private long _lastAccepted;
    private string? _incarnation;
    private string? _epoch;
    private long _position;
    private bool _hasBaseline;
    private long? _waitingSince;
    private long? _lastSyncAt;
    private long _lastActivityAt;

    public OrderingEngine(OrderingOptions options, long now)
    {
        ArgumentNullException.ThrowIfNull(options);
        _gapWait = (long)options.GapWait.TotalMilliseconds;
        _syncInterval = (long)options.SyncInterval.TotalMilliseconds;
        _syncRetry = (long)options.SyncRetry.TotalMilliseconds;
        _maxBuffered = options.MaxBuffered;
        _lastActivityAt = now;
    }

    /// <summary>What the client has processed so far; sent with a sync request.</summary>
    public StreamPosition Current => new(_incarnation, _epoch, _position);

    /// <summary>True once a snapshot has been processed.</summary>
    public bool Ready => _hasBaseline;

    /// <summary>
    /// How many snapshots have arrived so far, whatever became of them. Read it when a sync is sent and pass it to
    /// <see cref="ConfirmResynced"/>: it tells what arrived before the question from what arrived after it, which a
    /// clock cannot do for two things that happen within one of its ticks.
    /// </summary>
    public long SnapshotsReceived => _snapshotsReceived;

    /// <summary>Call when a snapshot should have arrived (after a successful join) but may have been lost.</summary>
    public void ExpectBaseline(long now)
    {
        if (!_hasBaseline)
        {
            _waitingSince ??= now;
        }
    }

    public IReadOnlyList<OrderingAction> ReceiveSnapshot(SnapshotEnvelope snapshot, long now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _lastActivityAt = now;
        _snapshotsReceived++;
        _refused = null;
        if (_hasBaseline)
        {
            if (IsCurrentStream(snapshot.Incarnation, snapshot.Epoch))
            {
                // An older baseline of the stream we are already following would move the app backwards.
                if (snapshot.At < _position)
                {
                    return [];
                }
            }
            else if (_retired.Contains((snapshot.Incarnation, snapshot.Epoch)))
            {
                // A stream the client has left does not come back, so this snapshot was on its way for a long time.
                // Following it would show the app an old state, and events it already has a second time. It is kept
                // for the one case in which the server says otherwise (see ConfirmResynced).
                _refused = snapshot;
                return [];
            }
        }

        return Accept(snapshot, now);
    }

    public IReadOnlyList<OrderingAction> ReceiveEvent(EventEnvelope @event, long now)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _lastActivityAt = now;

        // Before any snapshot, or from another epoch or incarnation: it can only be placed once a snapshot arrives.
        if (!_hasBaseline || !IsCurrentStream(@event.Incarnation, @event.Epoch))
        {
            Hold(@event);
            _waitingSince ??= now;
            return [];
        }

        if (@event.Seq <= _position)
        {
            return [];
        }

        if (@event.Seq == _position + 1)
        {
            _position = @event.Seq;
            List<OrderingAction> actions = [new EventAction(@event)];
            Drain(actions);
            UpdateWaiting(now);
            return actions;
        }

        // A gap. Hold the event and give the missing ones a moment to arrive.
        Hold(@event);
        _waitingSince ??= now;
        return [];
    }

    /// <summary>The server answered a sync with "in-sync": this stream is the current one and nothing up to the position is missing.</summary>
    public void ConfirmInSync(long now)
    {
        _lastActivityAt = now;

        // Anything held from another epoch or incarnation is stale, because the server just confirmed which stream is
        // current. Keeping it would make the client ask for a sync again and again. Later events of this stream were sent
        // after the server answered, so they stay: the ones before them arrive, or the next sync brings a snapshot.
        DropUnusable();
        UpdateWaiting(now);
    }

    /// <summary>
    /// The server answered a sync with "resynced": just before, it sent a snapshot of the stream that is current. If
    /// that snapshot was refused because it belongs to a stream the client had left, the client was wrong about which
    /// of the two streams is over (it had followed a late snapshot of an epoch it had never seen), and the snapshot is
    /// applied after all.
    /// </summary>
    /// <param name="snapshotsReceivedBefore">What <see cref="SnapshotsReceived"/> was when the sync was sent.</param>
    /// <param name="now">The current time.</param>
    public IReadOnlyList<OrderingAction> ConfirmResynced(long snapshotsReceivedBefore, long now)
    {
        // Only the last snapshot to arrive can be the answer (a refused one is forgotten when the next arrives), and
        // only if it came after the question. When a snapshot was applied since the sync was sent, that one was the answer.
        if (_refused is not { } snapshot || _snapshotsReceived <= snapshotsReceivedBefore || _lastAccepted > snapshotsReceivedBefore)
        {
            return [];
        }

        return Accept(snapshot, now);
    }

    /// <summary>Call regularly. Returns a <see cref="SyncAction"/> when the client should check itself against the server.</summary>
    public IReadOnlyList<OrderingAction> Tick(long now)
    {
        bool behind = _waitingSince is { } since && now - since >= _gapWait && (_lastSyncAt is not { } retried || now - retried >= _syncRetry);
        bool idle = _hasBaseline && now - _lastActivityAt >= _syncInterval && (_lastSyncAt is not { } synced || now - synced >= _syncInterval);
        if (!behind && !idle)
        {
            return [];
        }

        _lastSyncAt = now;
        return [SyncAction.Instance];
    }

    private bool IsCurrentStream(string incarnation, string epoch) => incarnation == _incarnation && epoch == _epoch;

    private List<OrderingAction> Accept(SnapshotEnvelope snapshot, long now)
    {
        if (_hasBaseline && !IsCurrentStream(snapshot.Incarnation, snapshot.Epoch))
        {
            // The stream the client leaves here is over. Only the last few are remembered: a snapshot is not on its
            // way for as long as it takes a room to restart that often.
            _retired.Add((_incarnation!, _epoch!));
            if (_retired.Count > MaxRetired)
            {
                _retired.RemoveAt(0);
            }
        }

        _retired.Remove((snapshot.Incarnation, snapshot.Epoch));
        _refused = null;
        _lastAccepted = _snapshotsReceived;
        _incarnation = snapshot.Incarnation;
        _epoch = snapshot.Epoch;
        _position = snapshot.At;
        _hasBaseline = true;
        DropUnusable();

        List<OrderingAction> actions = [new SnapshotAction(snapshot)];
        Drain(actions);
        UpdateWaiting(now);
        return actions;
    }

    private void DropUnusable() => _buffer.RemoveAll(held => !IsCurrentStream(held.Incarnation, held.Epoch) || held.Seq <= _position);

    private void Hold(EventEnvelope @event)
    {
        foreach (EventEnvelope held in _buffer)
        {
            if (held.Incarnation == @event.Incarnation && held.Epoch == @event.Epoch && held.Seq == @event.Seq)
            {
                return;
            }
        }

        _buffer.Add(@event);
        if (_buffer.Count > _maxBuffered)
        {
            _buffer.RemoveAt(0);
        }
    }

    private void Drain(List<OrderingAction> actions)
    {
        while (true)
        {
            int index = _buffer.FindIndex(held => IsCurrentStream(held.Incarnation, held.Epoch) && held.Seq == _position + 1);
            if (index < 0)
            {
                return;
            }

            EventEnvelope next = _buffer[index];
            _buffer.RemoveAt(index);
            _position = next.Seq;
            actions.Add(new EventAction(next));
        }
    }

    private void UpdateWaiting(long now)
    {
        if (_buffer.Count == 0)
        {
            _waitingSince = null;
        }
        else
        {
            _waitingSince ??= now;
        }
    }
}
