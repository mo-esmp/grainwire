using System.Collections.Immutable;
using System.Text.Json;
using GrainWire.Client.Internal;
using GrainWire.Client.Protocol;

namespace GrainWire.Client;

/// <summary>
/// A room this client has joined. Events arrive in order, each exactly once; after a reconnect or a lost message the
/// room catches up through a fresh snapshot. See <see cref="Room{TView}"/> for the view.
/// </summary>
/// <remarks>
/// Handlers are called one at a time, in order, on a thread-pool thread. A handler may call back into the room, but
/// must not block waiting for a request: the reply cannot be processed until the handler returns.
/// </remarks>
public abstract class Room
{
    private static readonly TimeSpan s_housekeepingTimeout = TimeSpan.FromSeconds(5);

    // Everything below is guarded by the client's lock. Methods named ...Locked expect the caller to hold it.
    private readonly OrderingEngine _engine;
    private readonly byte[]? _joinData;
    private readonly Dictionary<string, ImmutableArray<Action<JsonElement, EventMeta>>> _eventHandlers = [with(StringComparer.Ordinal)];
    private readonly List<ReadyWaiter> _readyWaiters = [];
    private readonly List<Action> _syncWaiters = [];
    private ImmutableArray<Action<string, JsonElement, EventMeta>> _anyHandlers = [];
    private ImmutableArray<Action<PresenceChange>> _presenceHandlers = [];
    private ImmutableArray<Action<ClosedInfo>> _closedHandlers = [];
    private List<Action>? _rejoinWaiters;
    private IReadOnlyList<Member> _members = [];
    private Member? _you;
    private RoomClosedReason? _closedReason;
    private bool _attached;
    private bool _joinedOnce;
    private bool _syncing;

    private protected Room(GrainWireClient client, string type, string id, byte[]? joinData)
    {
        Client = client;
        Type = type;
        Id = id;
        Key = $"{type}/{id}";
        _joinData = joinData;
        _engine = new OrderingEngine(
            new OrderingOptions { GapWait = client.Options.GapWait, SyncInterval = client.Options.SyncInterval },
            GrainWireClient.Now);
    }

    /// <summary>The room type, as in <c>[RoomType("chess")]</c> on the server.</summary>
    public string Type { get; }

    /// <summary>The room id.</summary>
    public string Id { get; }

    /// <summary>The room's name on the wire: <c>type/id</c>.</summary>
    public string Key { get; }

    /// <summary>The members of the room, kept up to date from presence events.</summary>
    public IReadOnlyList<Member> Members
    {
        get
        {
            lock (Client.Gate)
            {
                return _members;
            }
        }
    }

    /// <summary>This player's own membership (role, status).</summary>
    public Member? You
    {
        get
        {
            lock (Client.Gate)
            {
                return _you;
            }
        }
    }

    /// <summary>True once the room has ended for this client: it left, was kicked or removed, or the room closed or was lost.</summary>
    public bool IsClosed => ClosedReason is not null;

    /// <summary>Why the room ended for this client, or null while it is open.</summary>
    public RoomClosedReason? ClosedReason
    {
        get
        {
            lock (Client.Gate)
            {
                return _closedReason;
            }
        }
    }

    private protected GrainWireClient Client { get; }

    private protected IReadOnlyList<Member> MembersLocked => _members;

    private protected Member? YouLocked => _you;

    /// <summary>Calls <paramref name="handler"/> for every app event with this name, in order. Dispose the result to stop.</summary>
    public IDisposable On<TData>(string name, Action<TData, EventMeta> handler)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(handler);
        JsonSerializerOptions json = Client.Options.SerializerOptions!;
        Action<JsonElement, EventMeta> wrapped = (data, meta) => handler(Payloads.Deserialize<TData>(data, json)!, meta);
        lock (Client.Gate)
        {
            _eventHandlers[name] = _eventHandlers.GetValueOrDefault(name, []).Add(wrapped);
        }

        return new Subscription(() =>
        {
            lock (Client.Gate)
            {
                if (!_eventHandlers.TryGetValue(name, out ImmutableArray<Action<JsonElement, EventMeta>> handlers))
                {
                    return;
                }

                handlers = handlers.Remove(wrapped);
                if (handlers.IsEmpty)
                {
                    _eventHandlers.Remove(name);
                }
                else
                {
                    _eventHandlers[name] = handlers;
                }
            }
        });
    }

    /// <summary>Calls <paramref name="handler"/> for every app event with this name, in order. Dispose the result to stop.</summary>
    public IDisposable On<TData>(string name, Action<TData> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return On<TData>(name, (data, _) => handler(data));
    }

    /// <summary>Calls <paramref name="handler"/> for every app event, in order, with the event's name and raw data.</summary>
    public IDisposable OnAny(Action<string, JsonElement, EventMeta> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Client.Gate)
        {
            _anyHandlers = _anyHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (Client.Gate)
            {
                _anyHandlers = _anyHandlers.Remove(handler);
            }
        });
    }

    /// <summary>Calls <paramref name="handler"/> when a member joins, drops ("reconnecting"), comes back ("online"), leaves, or changes role.</summary>
    public IDisposable OnPresence(Action<PresenceChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Client.Gate)
        {
            _presenceHandlers = _presenceHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (Client.Gate)
            {
                _presenceHandlers = _presenceHandlers.Remove(handler);
            }
        });
    }

    /// <summary>Calls <paramref name="handler"/> once when the room ends for this client: closed, kicked, removed, or lost on the server.</summary>
    public IDisposable OnClosed(Action<ClosedInfo> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Client.Gate)
        {
            _closedHandlers = _closedHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (Client.Gate)
            {
                _closedHandlers = _closedHandlers.Remove(handler);
            }
        });
    }

    /// <summary>
    /// Sends a command and waits for the outcome. Completes with the handler's return value once the command has
    /// committed.
    /// </summary>
    /// <param name="name">The command name, as in <c>[OnMessage("move")]</c> on the server.</param>
    /// <param name="data">The command payload, serialized as JSON.</param>
    /// <param name="timeout">How long to wait for a reply. Defaults to <see cref="GrainWireClientOptions.RequestTimeout"/>.</param>
    /// <param name="cancellationToken">Stops waiting and re-sending. A command that was already sent may still be applied.</param>
    /// <exception cref="RejectedException">The room refused the command.</exception>
    /// <exception cref="RoomException">The command could not be processed.</exception>
    /// <exception cref="OutcomeUnknownException">
    /// No reply arrived in time. Until then the command is re-sent with the same command id, so it cannot run twice.
    /// </exception>
    /// <exception cref="RoomClosedException">The room is closed for this client.</exception>
    /// <exception cref="NotConnectedException">There is no connection right now.</exception>
    public async Task<TResult> RequestAsync<TResult>(string name, object? data = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        JsonElement result = await RequestCoreAsync(name, data, timeout, cancellationToken).ConfigureAwait(false);
        return Payloads.Deserialize<TResult>(result, Client.Options.SerializerOptions!)!;
    }

    /// <summary>Sends a command and waits until it has committed, ignoring any return value. See <see cref="RequestAsync{TResult}"/>.</summary>
    public Task RequestAsync(string name, object? data = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        RequestCoreAsync(name, data, timeout, cancellationToken);

    /// <summary>Sends a command without waiting for a reply. Nothing reports whether it was applied.</summary>
    /// <exception cref="RoomClosedException">The room is closed for this client.</exception>
    /// <exception cref="NotConnectedException">There is no connection right now.</exception>
    public void Send(string name, object? data = null)
    {
        Wire.EnsureName(name, nameof(name));
        byte[]? dataJson = Payloads.Serialize(data, Client.Options.SerializerOptions!);
        lock (Client.Gate)
        {
            EnsureOpenLocked();
            byte[] payload = ClientPayload.Command(GrainWireClient.NewCommandId(), _engine.Current.Incarnation, dataJson, noReply: true);
            Client.PublishLocked(Wire.RoomTopic(Key, Wire.CommandPrefix + name), payload);
        }
    }

    /// <summary>Checks this room against the server now, instead of waiting for the next automatic check.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Client.Gate)
        {
            EnsureOpenLocked();
            RunSyncLocked(() => done.TrySetResult());
        }

        Client.Callbacks.Drain();
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops following the room on this connection. The player stays a member until the server's grace period ends.</summary>
    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Client.Gate)
        {
            if (_closedReason is not null)
            {
                return;
            }

            CloseLocked(RoomClosedReason.Left, null, notify: false);
            PendingRequest? pending = Client.RequestLocked(RequestKind.Leave, Key, Wire.Leave, ClientPayload.Leave, s_housekeepingTimeout, (_, _) => done.TrySetResult());
            if (pending is null)
            {
                done.TrySetResult();
            }
        }

        Client.Callbacks.Drain();
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- driven by the client

    /// <summary>True once the server has accepted a join, also while the first snapshot is still on its way.</summary>
    internal bool NeedsRejoinLocked => _closedReason is null && !_attached && _joinedOnce;

    internal bool JoinedOnceLocked => _joinedOnce;

    internal RoomClosedReason? ClosedReasonLocked => _closedReason;

    internal void JoinedLocked(long now)
    {
        _attached = true;
        _joinedOnce = true;
        _engine.ExpectBaseline(now);
    }

    /// <summary>
    /// Calls <paramref name="done"/> once the first snapshot has been processed: with a <see cref="RoomClosedException"/>
    /// when the room closes first, and with an <see cref="OutcomeUnknownException"/> when no snapshot arrives by
    /// <paramref name="deadline"/>.
    /// </summary>
    internal void WhenReadyLocked(long deadline, string commandId, Action<Exception?> done)
    {
        if (_engine.Ready)
        {
            done(null);
        }
        else if (_closedReason is { } reason)
        {
            done(new RoomClosedException(reason));
        }
        else
        {
            _readyWaiters.Add(new ReadyWaiter(deadline, commandId, done));
        }
    }

    internal void ExpireWaitersLocked(long now)
    {
        if (_readyWaiters.Count == 0)
        {
            return;
        }

        foreach (ReadyWaiter waiter in _readyWaiters.ToArray())
        {
            if (now >= waiter.Deadline && _readyWaiters.Remove(waiter))
            {
                waiter.Done(new OutcomeUnknownException(waiter.CommandId));
            }
        }
    }

    internal void FailWaitersLocked(Exception error)
    {
        ReadyWaiter[] waiters = [.. _readyWaiters];
        _readyWaiters.Clear();
        foreach (ReadyWaiter waiter in waiters)
        {
            waiter.Done(error);
        }
    }

    internal void DisconnectedLocked()
    {
        _attached = false;
        _syncing = false;
    }

    /// <summary>
    /// Joins again after a reconnect. The server keeps the seat and sends a fresh snapshot. <paramref name="then"/> is
    /// called when the attempt is over, whatever its outcome.
    /// </summary>
    internal void RejoinLocked(Action? then)
    {
        if (_closedReason is not null)
        {
            then?.Invoke();
            return;
        }

        if (_rejoinWaiters is { } inFlight)
        {
            if (then is not null)
            {
                inFlight.Add(then);
            }

            return;
        }

        List<Action> waiters = [];
        if (then is not null)
        {
            waiters.Add(then);
        }

        _rejoinWaiters = waiters;
        PendingRequest? pending = Client.RequestLocked(RequestKind.Join, Key, Wire.Join, commandId => ClientPayload.Join(commandId, create: false, null, _joinData), null, (reply, _) =>
        {
            if (reply is not null)
            {
                if (reply.Status == ReplyStatus.Ok)
                {
                    JoinedLocked(GrainWireClient.Now);
                }
                else if (reply.Code == Wire.RoomNotFound)
                {
                    CloseLocked(RoomClosedReason.Lost, null, notify: true);
                }
                else if (reply.Status == ReplyStatus.Rejected || reply.Code == Wire.NotMember)
                {
                    CloseLocked(RoomClosedReason.Removed, reply.Reason, notify: true);
                }
            }

            // No reply (not connected, or timed out): the next reconnect tries again.
            FinishRejoinLocked(waiters);
        });

        if (pending is null)
        {
            FinishRejoinLocked(waiters);
        }
    }

    internal void ReceiveLocked(InboxEnvelope envelope, long now)
    {
        if (_closedReason is not null)
        {
            return;
        }

        switch (envelope)
        {
            case ClosedEnvelope closed:
                CloseLocked(closed.Reason, closed.Message, notify: true);
                break;
            case SnapshotEnvelope snapshot:
                ApplyLocked(_engine.ReceiveSnapshot(snapshot, now));
                break;
            case EventEnvelope @event:
                ApplyLocked(_engine.ReceiveEvent(@event, now));
                break;
        }
    }

    internal void TickLocked(long now)
    {
        if (_closedReason is not null || !_attached)
        {
            return;
        }

        if (_engine.Tick(now).Count > 0)
        {
            RunSyncLocked(null);
        }
    }

    internal void CloseLocked(RoomClosedReason reason, string? message, bool notify)
    {
        if (_closedReason is not null)
        {
            return;
        }

        _closedReason = reason;
        _attached = false;
        Client.ForgetLocked(this);
        FailWaitersLocked(new RoomClosedException(reason));
        if (notify && reason != RoomClosedReason.Left)
        {
            ClosedInfo info = new(reason, message);
            foreach (Action<ClosedInfo> handler in _closedHandlers)
            {
                Client.Callbacks.Enqueue(() => handler(info));
            }
        }
    }

    /// <summary>Called with every snapshot the ordering engine accepts, after the member list has been replaced.</summary>
    private protected abstract void SnapshotAppliedLocked(SnapshotEnvelope snapshot, SnapshotInfo info);

    private async Task<JsonElement> RequestCoreAsync(string name, object? data, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        Wire.EnsureName(name, nameof(name));
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? dataJson = Payloads.Serialize(data, Client.Options.SerializerOptions!);
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingRequest pending;
        lock (Client.Gate)
        {
            EnsureOpenLocked();
            string? incarnation = _engine.Current.Incarnation;
            pending = Client.RequestLocked(RequestKind.Command, Key, Wire.CommandPrefix + name, commandId => ClientPayload.Command(commandId, incarnation, dataJson, noReply: false), timeout, (reply, error) =>
            {
                Exception? failure = reply is null ? error : InterpretLocked(reply);
                if (failure is OperationCanceledException cancelled)
                {
                    completion.TrySetCanceled(cancelled.CancellationToken);
                }
                else if (failure is not null)
                {
                    completion.TrySetException(failure);
                }
                else
                {
                    completion.TrySetResult(reply!.Data);
                }
            })!;
        }

        Client.Callbacks.Drain();
        using (cancellationToken.Register(() => Client.Abandon(pending, new OperationCanceledException(cancellationToken))))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    private void RunSyncLocked(Action? done)
    {
        if (_closedReason is not null || !_attached)
        {
            done?.Invoke();
            return;
        }

        if (done is not null)
        {
            _syncWaiters.Add(done);
        }

        if (_syncing)
        {
            return;
        }

        _syncing = true;
        StreamPosition position = _engine.Current;
        long seq = _engine.Ready ? position.Seq : -1;
        PendingRequest? pending = Client.RequestLocked(RequestKind.Sync, Key, Wire.Sync, commandId => ClientPayload.Sync(commandId, position.Incarnation, position.Epoch, seq), s_housekeepingTimeout, (reply, _) =>
        {
            if (reply is not null)
            {
                if (reply.Status == ReplyStatus.Ok)
                {
                    if (reply.Code == Wire.InSync)
                    {
                        _engine.ConfirmInSync(GrainWireClient.Now);
                    }
                }
                else if (reply.Code == Wire.RoomNotFound)
                {
                    CloseLocked(RoomClosedReason.Lost, null, notify: true);
                }
                else if (reply.Code == Wire.NotMember)
                {
                    CloseLocked(RoomClosedReason.Removed, null, notify: true);
                }
                else if (reply.Code == Wire.NotJoined)
                {
                    _attached = false;
                    RejoinLocked(null);
                }
            }

            // No reply: the engine asks again.
            FinishSyncLocked();
        });

        if (pending is null)
        {
            FinishSyncLocked();
        }
    }

    private void FinishSyncLocked()
    {
        _syncing = false;
        Action[] waiters = [.. _syncWaiters];
        _syncWaiters.Clear();
        foreach (Action waiter in waiters)
        {
            waiter();
        }
    }

    private void FinishRejoinLocked(List<Action> waiters)
    {
        if (ReferenceEquals(_rejoinWaiters, waiters))
        {
            _rejoinWaiters = null;
        }

        foreach (Action waiter in waiters.ToArray())
        {
            waiter();
        }
    }

    private void ApplyLocked(IReadOnlyList<OrderingAction> actions)
    {
        foreach (OrderingAction action in actions)
        {
            switch (action)
            {
                case SnapshotAction { Snapshot: { } snapshot }:
                    _members = Array.AsReadOnly(snapshot.Members.ToArray());
                    _you = snapshot.You;
                    ReadyWaiter[] waiters = [.. _readyWaiters];
                    _readyWaiters.Clear();
                    foreach (ReadyWaiter waiter in waiters)
                    {
                        waiter.Done(null);
                    }

                    SnapshotAppliedLocked(snapshot, new SnapshotInfo(_members, _you));
                    break;
                case EventAction { Event: { } @event }:
                    DeliverLocked(@event);
                    break;
            }
        }
    }

    private void DeliverLocked(EventEnvelope @event)
    {
        if (@event.Type == Wire.Presence)
        {
            if (InboxParser.Presence(@event.Data) is not { } presence)
            {
                return;
            }

            List<Member> members = [.. _members.Where(member => member.Player != presence.Player)];
            if (presence.Status != MemberStatus.Left)
            {
                members.Add(new Member(presence.Player, presence.Role, presence.Status));
            }

            _members = members.AsReadOnly();
            if (_you is { } you && you.Player == presence.Player)
            {
                _you = you with { Role = presence.Role, Status = presence.Status };
            }

            foreach (Action<PresenceChange> handler in _presenceHandlers)
            {
                Client.Callbacks.Enqueue(() => handler(presence));
            }

            return;
        }

        if (@event.Type.StartsWith('$'))
        {
            return;
        }

        string name = @event.Type;
        JsonElement data = @event.Data;
        EventMeta meta = new(@event.Seq);
        if (_eventHandlers.TryGetValue(name, out ImmutableArray<Action<JsonElement, EventMeta>> handlers))
        {
            foreach (Action<JsonElement, EventMeta> handler in handlers)
            {
                Client.Callbacks.Enqueue(() => handler(data, meta));
            }
        }

        foreach (Action<string, JsonElement, EventMeta> handler in _anyHandlers)
        {
            Client.Callbacks.Enqueue(() => handler(name, data, meta));
        }
    }

    /// <summary>Returns the error a command's reply stands for, or null when it succeeded. Some errors also mean the room is gone.</summary>
    private Exception? InterpretLocked(ReplyEnvelope reply)
    {
        if (reply.Status == ReplyStatus.Error)
        {
            if (reply.Code is Wire.RoomNotFound or Wire.StaleRoom)
            {
                CloseLocked(RoomClosedReason.Lost, null, notify: true);
            }
            else if (reply.Code == Wire.NotMember)
            {
                CloseLocked(RoomClosedReason.Removed, null, notify: true);
            }
        }

        return GrainWireClient.ToException(reply);
    }

    private void EnsureOpenLocked()
    {
        if (_closedReason is { } reason)
        {
            throw new RoomClosedException(reason);
        }

        if (!Client.IsConnectedLocked)
        {
            throw new NotConnectedException();
        }
    }
}
