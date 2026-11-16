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
    private readonly long _gapWait;
    private readonly long _syncInterval;
    private readonly long _syncRetry;
    private readonly int _maxBuffered;
    private readonly List<EventEnvelope> _buffer = [];
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

        // An older baseline of the stream we are already following would move the app backwards.
        if (_hasBaseline && snapshot.Incarnation == _incarnation && snapshot.Epoch == _epoch && snapshot.At < _position)
        {
            return [];
        }

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

    public IReadOnlyList<OrderingAction> ReceiveEvent(EventEnvelope @event, long now)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _lastActivityAt = now;

        // Before any snapshot, or from another epoch or incarnation: it can only be placed once a snapshot arrives.
        if (!_hasBaseline || !IsCurrentStream(@event))
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

    private bool IsCurrentStream(EventEnvelope @event) => @event.Incarnation == _incarnation && @event.Epoch == _epoch;

    private void DropUnusable() => _buffer.RemoveAll(held => !IsCurrentStream(held) || held.Seq <= _position);

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
            int index = _buffer.FindIndex(held => IsCurrentStream(held) && held.Seq == _position + 1);
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
