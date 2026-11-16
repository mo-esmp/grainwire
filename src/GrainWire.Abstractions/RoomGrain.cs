using System.Diagnostics.CodeAnalysis;
using GrainWire.Internal;
using GrainWire.Protocol;
using GrainWire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace GrainWire;

/// <summary>
/// Base class for a room: one grain owns the state of one room and changes it one command at a time.
/// Derive from it, add <see cref="RoomTypeAttribute"/>, and write handlers marked with <see cref="OnMessageAttribute"/>.
/// </summary>
/// <remarks>
/// Everything a handler sends (<see cref="Broadcast"/>, <see cref="SendTo"/>, <see cref="Kick"/> ...) is held back until
/// the command has committed. If the handler calls <see cref="Reject"/> or throws, the state is restored and nothing is sent.
/// </remarks>
public abstract class RoomGrain<TState> : Grain, IRoomGrain where TState : class, new()
{
    private const string StateName = "grainwire-room";
    private static readonly IReadOnlyDictionary<string, string> s_noClaims = new Dictionary<string, string>();

    private readonly RoomTypeInfo _info;
    private readonly RoomOptions _options;
    private readonly IRoomSerializer _serializer;
    private readonly DeepCopier _copier;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly IPersistentState<RoomRecord<TState>>? _storage;
    private RoomRecord<TState> _memory = new();

    // Not durable, by design: a new stream epoch and fresh presence on every activation.
    private readonly string _epoch = NewId();

    // Ranks this epoch against the room's other epochs for gateways (see DeliveryBatch.EpochOrder). It starts from the
    // clock, and is raised when a gateway reports an epoch ranked at or above it, because clocks differ between silos.
    private long _epochOrder;
    private readonly Dictionary<string, MemberRuntime> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _connectionOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GatewayLease> _gateways = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IGrainTimer> _deadlineTimers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deadlineFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _batchNumbers = new(StringComparer.Ordinal);
    private Operation? _op;

    protected RoomGrain()
    {
        _info = RoomTypeInfo.For(GetType());
        IServiceProvider services = GrainContext.ActivationServices;
        _options = services.GetService<IOptionsMonitor<RoomOptions>>()?.Get(_info.RoomType) ?? new RoomOptions();
        _serializer = services.GetService<IRoomSerializer>() ?? new JsonRoomSerializer();
        _copier = services.GetRequiredService<DeepCopier>();
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _epochOrder = _time.GetUtcNow().UtcTicks;
        _log = services.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        if (_options.StorageProvider is { } provider)
        {
            _storage = services.GetRequiredService<IPersistentStateFactory>()
                .Create<RoomRecord<TState>>(GrainContext, new PersistentStateAttribute(StateName, provider));
        }

        // Runs after stored state is loaded and before OnActivateAsync.
        GrainContext.ObservableLifecycle.Subscribe(GetType().FullName!, GrainLifecycleStage.SetupState + 1, OnRoomActivating);
    }

    // ---------------------------------------------------------------- what handlers can use

    /// <summary>The room's state. Changes made by a handler are kept only if the command commits.</summary>
    protected TState State
    {
        get => Record.State;
        set => Record.State = value;
    }

    protected string RoomType => _info.RoomType;

    protected string RoomId => this.GetPrimaryKeyString();

    protected DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The members of the room with their roles and presence.</summary>
    protected IReadOnlyList<RoomMember> Members
    {
        get
        {
            List<RoomMember> list = new(Record.Members.Count);
            foreach (MemberRecord member in Record.Members.Values)
            {
                list.Add(ToRoomMember(member));
            }

            return list;
        }
    }

    protected RoomMember? FindMember(PlayerId player) => Record.Members.TryGetValue(player.Value, out MemberRecord? member) ? ToRoomMember(member) : null;

    /// <summary>Decides whether <see cref="CreateContext.Creator"/> may create this room. Default: allow.</summary>
    protected virtual Task<CreateResult> OnCreate(CreateContext context) => Task.FromResult(CreateResult.Allow());

    /// <summary>Decides whether a new player may join, and with which role. Default: accept without a role.</summary>
    protected virtual Task<JoinResult> OnJoin(JoinContext context) => Task.FromResult(JoinResult.Accept());

    /// <summary>Re-checks access when an existing member comes back (bans, revoked invites). The role is kept. Default: accept.</summary>
    protected virtual Task<JoinResult> OnRejoin(JoinContext context) => Task.FromResult(JoinResult.Accept());

    /// <summary>Called when a member goes online or starts reconnecting. Other members are told automatically.</summary>
    protected virtual Task OnPresenceChanged(PlayerId player, PresenceStatus status) => Task.CompletedTask;

    /// <summary>Called when a member's grace period ran out, just before they are removed.</summary>
    protected virtual Task OnPlayerLeft(PlayerId player, LeaveReason reason) => Task.CompletedTask;

    /// <summary>Called when a deadline set with <see cref="Schedule(string, DateTimeOffset)"/> is due.</summary>
    protected virtual Task OnDeadline(string name) => Task.CompletedTask;

    /// <summary>What <paramref name="viewer"/> is allowed to see of the room. Default: the whole state.</summary>
    protected virtual object? GetSnapshot(PlayerId viewer) => State;

    /// <summary>Sends an event to every member (optionally all but one) once the current command has committed.</summary>
    protected void Broadcast(string name, object? data = null, PlayerId? except = null)
    {
        CurrentScope().Events.Add(new OutEvent { Type = EventName(name), Data = _serializer.Serialize(data), Except = except?.Value });
    }

    /// <summary>Sends an event to one member once the current command has committed.</summary>
    protected void SendTo(PlayerId player, string name, object? data = null)
    {
        CurrentScope().Events.Add(new OutEvent { Type = EventName(name), Data = _serializer.Serialize(data), Only = player.Value });
    }

    /// <summary>Ends the current command with a rejection. Nothing the handler did is kept or sent.</summary>
    [DoesNotReturn]
    protected void Reject(string reason, string? code = null) => throw new RoomRejectedException(reason, code);

    /// <summary>Changes a member's role when the current command commits.</summary>
    protected void SetRole(PlayerId player, string? role) => CurrentScope().RoleChanges.Add((player.Value, role));

    /// <summary>Removes a member when the current command commits and tells their connections why.</summary>
    protected void Kick(PlayerId player, string? reason = null) =>
        CurrentScope().Removals.Add(new Removal(player.Value, Wire.ClosedReasons.Kicked, reason));

    /// <summary>Removes a member when the current command commits (for example because they resigned their seat).</summary>
    protected void RemoveMember(PlayerId player) =>
        CurrentScope().Removals.Add(new Removal(player.Value, Wire.ClosedReasons.Removed, null));

    /// <summary>Closes the room for everyone and deletes it when the current command commits.</summary>
    protected void CloseRoom(string? reason = null)
    {
        Scope scope = CurrentScope();
        scope.Close = true;
        scope.CloseMessage = reason;
    }

    /// <summary>Sets (or moves) a named deadline. It survives restarts when the room is persistent. <see cref="OnDeadline"/> runs when it is due.</summary>
    protected void Schedule(string name, DateTimeOffset dueAt) => CurrentScope().DeadlineChanges.Add((name, dueAt));

    /// <inheritdoc cref="Schedule(string, DateTimeOffset)"/>
    protected void Schedule(string name, TimeSpan delay) => Schedule(name, Now + delay);

    protected void CancelSchedule(string name) => CurrentScope().DeadlineChanges.Add((name, null));

    /// <summary>
    /// Runs <paramref name="action"/> as one committed room operation. Use it from your own grain methods or timers so that
    /// state changes and events get the same commit-then-publish treatment as command handlers.
    /// </summary>
    protected async Task ExecuteAsync(Func<Task> action)
    {
        if (_op is not null)
        {
            await action();
            return;
        }

        Operation op = Begin();
        try
        {
            Exception? failure = await Guarded(action);
            if (failure is not null)
            {
                throw failure;
            }

            op.NeedsSave = true;
            if (!await CommitAsync(op))
            {
                throw new RoomNotCommittedException();
            }
        }
        finally
        {
            End();
        }
    }

    // ---------------------------------------------------------------- gateway-facing operations

    async Task<RoomReply> IRoomGrain.Join(JoinRequest request)
    {
        TouchGateway(request.Gateway, request.Known);
        string player = request.Player.Value;
        IReadOnlyDictionary<string, string> claims = (IReadOnlyDictionary<string, string>?)request.Claims ?? s_noClaims;
        Operation op = Begin();
        try
        {
            bool created = false;
            if (!Record.Exists)
            {
                if (!request.Create)
                {
                    DeactivateOnIdle();
                    return RoomReply.Error(Wire.Codes.RoomNotFound);
                }

                CreateResult? creation = null;
                Exception? createFailure = await Guarded(async () => creation = await OnCreate(new CreateContext(request.Player, claims, Payload(request.Options))));
                if (createFailure is not null || creation is not { Allowed: true })
                {
                    DeactivateOnIdle();
                    return createFailure is not null
                        ? FailureReply(createFailure, Wire.Codes.CreateDenied, nameof(OnCreate))
                        : RoomReply.Rejected(Wire.Codes.CreateDenied, creation?.Reason);
                }

                Record.Exists = true;
                Record.Incarnation = NewId();
                Record.CreatedAt = Now;
                created = true;
                op.NeedsSave = true;
            }

            JoinContext context = new(request.Player, claims, Payload(request.Data), created);
            if (Record.Members.ContainsKey(player))
            {
                if (_info.OverridesOnRejoin)
                {
                    JoinResult? rejoin = null;
                    Exception? failure = await Guarded(async () => rejoin = await OnRejoin(context));
                    if (failure is not null)
                    {
                        return FailureReply(failure, Wire.Codes.JoinRejected, nameof(OnRejoin));
                    }

                    if (rejoin is not { Accepted: true })
                    {
                        return RoomReply.Rejected(Wire.Codes.JoinRejected, rejoin?.Reason);
                    }

                    op.NeedsSave = true;
                }
            }
            else
            {
                RoomReply? refusal = null;
                JoinResult? join = null;
                if (Record.Members.Count >= _options.MaxMembers)
                {
                    refusal = RoomReply.Rejected(Wire.Codes.RoomFull, "The room is full.");
                }
                else
                {
                    Exception? failure = await Guarded(async () => join = await OnJoin(context));
                    if (failure is not null)
                    {
                        refusal = FailureReply(failure, Wire.Codes.JoinRejected, nameof(OnJoin));
                    }
                    else if (join is not { Accepted: true })
                    {
                        refusal = RoomReply.Rejected(Wire.Codes.JoinRejected, join?.Reason);
                    }
                }

                if (refusal is not null)
                {
                    if (created)
                    {
                        UndoCreate();
                    }

                    return refusal;
                }

                Record.Members[player] = new MemberRecord { Player = player, Role = join!.Role, JoinedAt = Now };
                _runtime[player] = new MemberRuntime { Status = PresenceStatus.Online };
                op.Current.Events.Add(PresenceEvent(player, PresenceStatus.Online, join.Role));
                op.NeedsSave = true;
            }

            Attach(request.Connection, player, request.Gateway.Id.Value);
            if (!await CommitAsync(op))
            {
                return RoomReply.Error(Wire.Codes.NotCommitted);
            }

            RoomReply reply = RoomReply.Ok(created ? Wire.Codes.Created : Wire.Codes.Joined);
            reply.Incarnation = Record.Incarnation;
            reply.Snapshot = BuildSnapshot(player);
            return reply;
        }
        finally
        {
            End();
        }
    }

    async Task<RoomReply> IRoomGrain.Command(CommandRequest request)
    {
        TouchGateway(request.Gateway);
        string player = request.Player.Value;
        if (!Record.Exists)
        {
            DeactivateOnIdle();
            return RoomReply.Error(Wire.Codes.RoomNotFound);
        }

        // 1. Validate. The grain is the authority; the gateway's own checks can be stale.
        if (request.Incarnation is { } incarnation && incarnation != Record.Incarnation)
        {
            return RoomReply.Error(Wire.Codes.StaleRoom);
        }

        if (!Record.Members.TryGetValue(player, out MemberRecord? member))
        {
            return RoomReply.Error(Wire.Codes.NotMember);
        }

        if (!_info.Handlers.TryGetValue(request.Name, out CommandHandler? handler))
        {
            return RoomReply.Error(Wire.Codes.UnknownCommand, $"This room has no command '{request.Name}'.");
        }

        if (handler.Roles is not null && (member.Role is null || !handler.Roles.Contains(member.Role)))
        {
            return RoomReply.Rejected(Wire.Codes.Forbidden, "Your role may not send this command.");
        }

        // 2. De-duplicate: a retried command returns its recorded outcome and does nothing else.
        if (request.CommandId is { } commandId && TryGetRecorded(player, commandId, out RoomReply? recorded))
        {
            return recorded;
        }

        Operation op = Begin();
        try
        {
            bool attached = Attach(request.Connection, player, request.Gateway.Id.Value);

            // 3 + 4. Protect state, run the handler with its outbox. Turning the result into bytes is part of this
            // step: if that fails, the handler's changes are undone like for any other failure.
            byte[]? data = null;
            Exception? failure = await Guarded(async () =>
            {
                object? result = await handler.Invoke(this, request.Player, request.Data, _serializer);
                data = _serializer.Serialize(result);
            });

            // 5 + 6. Outcome, commit, then release.
            RoomReply reply;
            if (failure is not null)
            {
                reply = FailureReply(failure, Wire.Codes.Rejected, request.Name);
                await CommitAsync(op);
            }
            else
            {
                reply = RoomReply.Ok(data: data);
                reply.Incarnation = Record.Incarnation;
                op.NeedsSave = true;
                if (request.CommandId is not null)
                {
                    Remember(player, request.CommandId, reply);
                }

                if (!await CommitAsync(op))
                {
                    reply = RoomReply.Error(Wire.Codes.NotCommitted);
                }
            }

            if (attached && Record.Exists && Record.Members.ContainsKey(player))
            {
                reply = reply.WithoutSnapshot();
                reply.Snapshot = BuildSnapshot(player);
            }

            return reply;
        }
        finally
        {
            End();
        }
    }

    async Task<RoomReply> IRoomGrain.Sync(SyncRequest request)
    {
        TouchGateway(request.Gateway, request.Known);
        string player = request.Player.Value;
        if (!Record.Exists)
        {
            DeactivateOnIdle();
            return RoomReply.Error(Wire.Codes.RoomNotFound);
        }

        if (!Record.Members.ContainsKey(player))
        {
            return RoomReply.Error(Wire.Codes.NotMember);
        }

        Operation op = Begin();
        try
        {
            bool attached = Attach(request.Connection, player, request.Gateway.Id.Value);
            await CommitAsync(op);

            long position = _runtime[player].Seq;
            if (!attached && request.Incarnation == Record.Incarnation && request.Epoch == _epoch && request.Seq == position)
            {
                return RoomReply.Ok(Wire.Codes.InSync);
            }

            RoomReply reply = RoomReply.Ok(Wire.Codes.Resynced);
            reply.Incarnation = Record.Incarnation;
            reply.Snapshot = BuildSnapshot(player);
            return reply;
        }
        finally
        {
            End();
        }
    }

    async Task<RoomReply> IRoomGrain.Leave(LeaveRequest request)
    {
        if (!Record.Exists)
        {
            DeactivateOnIdle();
            return RoomReply.Error(Wire.Codes.RoomNotFound);
        }

        Operation op = Begin();
        try
        {
            DetachConnection(request.Connection);
            await CommitAsync(op);
            return RoomReply.Ok(Wire.Codes.Left);
        }
        finally
        {
            End();
        }
    }

    async Task IRoomGrain.Detach(DetachRequest request)
    {
        if (!Record.Exists)
        {
            DeactivateOnIdle();
            return;
        }

        Operation op = Begin();
        try
        {
            foreach (ConnectionId connection in request.Connections)
            {
                DetachConnection(connection);
            }

            await CommitAsync(op);
        }
        finally
        {
            End();
        }
    }

    async Task<RenewReply> IRoomGrain.Renew(RenewRequest request)
    {
        RenewReply reply = new() { RoomExists = Record.Exists };
        if (!Record.Exists)
        {
            DeactivateOnIdle();
            return reply;
        }

        TouchGateway(request.Gateway, request.Known);
        string gateway = request.Gateway.Id.Value;
        Operation op = Begin();
        try
        {
            List<(ConnectionId Connection, string Player)>? newlyAttached = null;
            foreach (RenewEntry entry in request.Connections)
            {
                if (!Record.Members.ContainsKey(entry.Player.Value))
                {
                    reply.NotMembers.Add(entry.Connection);
                }
                else if (Attach(entry.Connection, entry.Player.Value, gateway))
                {
                    (newlyAttached ??= []).Add((entry.Connection, entry.Player.Value));
                }
            }

            await CommitAsync(op);

            if (newlyAttached is not null)
            {
                foreach ((ConnectionId connection, string? player) in newlyAttached)
                {
                    if (Record.Members.ContainsKey(player))
                    {
                        reply.Snapshots.Add(new ConnectionSnapshot { Connection = connection, Snapshot = BuildSnapshot(player) });
                    }
                }
            }

            return reply;
        }
        finally
        {
            End();
        }
    }

    // ---------------------------------------------------------------- the pipeline

    private RoomRecord<TState> Record => _storage is not null ? _storage.State : _memory;

    private Operation Begin()
    {
        if (_op is not null)
        {
            throw new InvalidOperationException("A room operation is already running. Room grains must not be reentrant.");
        }

        return _op = new Operation();
    }

    private void End() => _op = null;

    private Scope CurrentScope() => (_op ?? throw new InvalidOperationException(
        "This can only be called while the room is handling a command, a hook, or inside ExecuteAsync.")).Current;

    /// <summary>
    /// Runs app code with its own outbox and a copy of the state. On failure the state is restored and the outbox dropped;
    /// the exception is returned instead of thrown.
    /// </summary>
    private async Task<Exception?> Guarded(Func<Task> body)
    {
        Operation op = _op!;
        Scope outer = op.Current;
        Scope scope = new();
        op.Current = scope;
        TState? backup = _options.Rollback == RollbackMode.Copy ? _copier.Copy(Record.State) : null;
        try
        {
            await body();
            scope.MergeInto(outer);
            return null;
        }
        catch (Exception ex)
        {
            if (backup is not null)
            {
                Record.State = backup;
            }
            else if (ex is not RoomRejectedException)
            {
                // No copy to go back to. A rejection is trusted to have changed nothing (that is the RollbackMode.None contract);
                // anything else may have left the state half-changed, so go back to what was last saved.
                if (_storage is not null)
                {
                    await ReloadAsync();
                    op.Poisoned = true;
                }
                else
                {
                    _log.LogError(ex, "Room {Room} uses RollbackMode.None and a handler threw: its state may be inconsistent.", RoomId);
                }
            }

            return ex;
        }
        finally
        {
            op.Current = outer;
        }
    }

    /// <summary>Applies what the operation decided, saves it, and only then lets events out.</summary>
    private async Task<bool> CommitAsync(Operation op)
    {
        if (op.Poisoned)
        {
            return false;
        }

        if (_info.OverridesOnPresenceChanged && op.PresenceChanges.Count > 0)
        {
            foreach ((string? player, PresenceStatus status) in op.PresenceChanges.ToArray())
            {
                Exception? failure = await Guarded(() => OnPresenceChanged(new PlayerId(player), status));
                if (failure is not null)
                {
                    _log.LogError(failure, "OnPresenceChanged failed in room {Room}.", RoomId);
                }
                else
                {
                    op.NeedsSave = true;
                }
            }

            if (op.Poisoned)
            {
                return false;
            }
        }

        Scope scope = op.Current;
        RoomRecord<TState> record = Record;

        foreach ((string? player, string? role) in scope.RoleChanges)
        {
            if (!record.Members.TryGetValue(player, out MemberRecord? member) || member.Role == role)
            {
                continue;
            }

            member.Role = role;
            scope.Events.Add(PresenceEvent(player, _runtime.TryGetValue(player, out MemberRuntime? rt) ? rt.Status : PresenceStatus.Reconnecting, role, includeSelf: true));
        }

        foreach ((string? name, DateTimeOffset? due) in scope.DeadlineChanges)
        {
            if (due is { } at)
            {
                record.Deadlines[name] = at;
            }
            else
            {
                record.Deadlines.Remove(name);
            }
        }

        List<Removal>? removed = null;
        foreach (Removal removal in scope.Removals)
        {
            if (!record.Members.Remove(removal.Player))
            {
                continue;
            }

            record.Dedup.Remove(removal.Player);
            (removed ??= []).Add(removal);
        }

        if (_storage is not null && op.NeedsSave)
        {
            try
            {
                if (scope.Close)
                {
                    await _storage.ClearStateAsync();
                }
                else
                {
                    await _storage.WriteStateAsync();
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Room {Room} could not save; the operation was rolled back.", RoomId);
                await ReloadAsync();
                return false;
            }
        }

        // Committed. From here on nothing can fail the operation.
        foreach ((string? name, DateTimeOffset? due) in scope.DeadlineChanges)
        {
            _deadlineFailures.Remove(name);
            if (due is { } at)
            {
                ArmDeadline(name, at);
            }
            else
            {
                DisarmDeadline(name);
            }
        }

        // Everything this operation sends goes out as one numbered batch per gateway, so gateways can keep the order.
        Dictionary<string, DeliveryBatch> batches = new(StringComparer.Ordinal);
        Stamp(scope.Events, batches);

        if (removed is not null)
        {
            List<OutEvent> followUp = [];
            foreach (Removal removal in removed)
            {
                if (!_runtime.Remove(removal.Player, out MemberRuntime? runtime))
                {
                    continue;
                }

                runtime.GraceTimer?.Dispose();
                if (removal.ClosedReason is not null && runtime.Connections.Count > 0)
                {
                    followUp.Add(ClosedNotice(removal.ClosedReason, removal.Message, [.. runtime.Connections.Select(c => (c.Value.Gateway, c.Key))]));
                }

                foreach (string clientId in runtime.Connections.Keys)
                {
                    _connectionOwner.Remove(clientId);
                }

                followUp.Add(PresenceEvent(removal.Player, PresenceStatus.Left, null));
            }

            Stamp(followUp, batches);
        }

        if (scope.Close)
        {
            List<(string Gateway, string Key)> everyone = [.. _runtime.Values.SelectMany(r => r.Connections.Select(c => (c.Value.Gateway, c.Key)))];
            if (everyone.Count > 0)
            {
                Stamp([ClosedNotice(Wire.ClosedReasons.Closed, scope.CloseMessage, everyone)], batches);
            }

            Send(batches);
            Teardown();
        }
        else
        {
            Send(batches);
            if (_storage is null && record.Exists && record.Members.Count == 0)
            {
                // An in-memory room with nobody in it ceases to exist.
                Teardown();
            }
        }

        return true;
    }

    /// <summary>Gives each event its per-player sequence numbers and sorts it into the batch of each gateway involved.</summary>
    private void Stamp(List<OutEvent> events, Dictionary<string, DeliveryBatch> batches)
    {
        foreach (OutEvent outgoing in events)
        {
            Dictionary<string, DeliveryItem>? items = null;

            void Add(string gateway, string clientId, long seq)
            {
                items ??= new(StringComparer.Ordinal);
                if (!items.TryGetValue(gateway, out DeliveryItem? item))
                {
                    item = new DeliveryItem { Type = outgoing.Type, Data = outgoing.Data, Sequenced = outgoing.Sequenced };
                    items[gateway] = item;
                    if (!batches.TryGetValue(gateway, out DeliveryBatch? batch))
                    {
                        batch = new DeliveryBatch { Room = $"{RoomType}/{RoomId}", Incarnation = Record.Incarnation ?? "", Epoch = _epoch };
                        batches[gateway] = batch;
                    }

                    batch.Items.Add(item);
                }

                item.Targets.Add(new DeliveryTarget { ClientId = clientId, Seq = seq });
            }

            if (outgoing.Targets is not null)
            {
                foreach ((string? gateway, string? clientId) in outgoing.Targets)
                {
                    Add(gateway, clientId, 0);
                }

                continue;
            }

            if (outgoing.Only is not null)
            {
                if (_runtime.TryGetValue(outgoing.Only, out MemberRuntime? only))
                {
                    Advance(only);
                }

                continue;
            }

            foreach ((string? player, MemberRuntime? runtime) in _runtime)
            {
                if (player != outgoing.Except)
                {
                    Advance(runtime);
                }
            }

            void Advance(MemberRuntime runtime)
            {
                // Every member's position advances, attached or not: a returning member's snapshot starts from it.
                long seq = ++runtime.Seq;
                foreach ((string? clientId, AttachedConnection connection) in runtime.Connections)
                {
                    Add(connection.Gateway, clientId, seq);
                }
            }
        }
    }

    private void Send(Dictionary<string, DeliveryBatch> batches)
    {
        foreach ((string? gateway, DeliveryBatch? batch) in batches)
        {
            if (!_gateways.TryGetValue(gateway, out GatewayLease? lease))
            {
                continue;
            }

            // One-way calls can arrive out of order. The number lets the gateway put a room's batches back in order.
            _batchNumbers[gateway] = batch.Number = _batchNumbers.GetValueOrDefault(gateway) + 1;
            batch.EpochOrder = _epochOrder;
            try
            {
                // One-way: the room does not wait for delivery. A lost batch is repaired by the client's sync.
                _ = lease.Observer.Deliver(batch);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Room {Room} could not hand a batch to gateway {Gateway}.", RoomId, gateway);
            }
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            await _storage!.ReadStateAsync();
        }
        catch (Exception ex)
        {
            // What is in memory was never saved and the saved state cannot be read: start over on the next call.
            _log.LogError(ex, "Room {Room} could not reload its saved state; deactivating.", RoomId);
            DeactivateOnIdle();
        }

        ReconcileRuntime(retryDelay: TimeSpan.FromSeconds(1));
    }

    /// <summary>Brings the non-durable parts (presence, timers) back in line with the record.</summary>
    private void ReconcileRuntime(TimeSpan retryDelay)
    {
        RoomRecord<TState> record = Record;
        foreach (string? player in _runtime.Keys.Where(p => !record.Exists || !record.Members.ContainsKey(p)).ToList())
        {
            if (!_runtime.Remove(player, out MemberRuntime? runtime))
            {
                continue;
            }

            runtime.GraceTimer?.Dispose();
            foreach (string clientId in runtime.Connections.Keys)
            {
                _connectionOwner.Remove(clientId);
            }
        }

        foreach (string? name in _deadlineTimers.Keys.ToList())
        {
            DisarmDeadline(name);
        }

        if (!record.Exists)
        {
            return;
        }

        foreach (string player in record.Members.Keys)
        {
            if (!_runtime.TryGetValue(player, out MemberRuntime? runtime))
            {
                _runtime[player] = runtime = new MemberRuntime { Status = PresenceStatus.Reconnecting };
            }

            if (runtime.Connections.Count == 0 && runtime.GraceTimer is null)
            {
                runtime.Status = PresenceStatus.Reconnecting;
                StartGrace(player, runtime);
            }
        }

        DateTimeOffset earliest = Now + retryDelay;
        foreach ((string? name, DateTimeOffset due) in record.Deadlines)
        {
            ArmDeadline(name, due < earliest ? earliest : due);
        }
    }

    private Task OnRoomActivating(CancellationToken cancellationToken)
    {
        // After a restart every member starts as "reconnecting" with a fresh grace period, so nobody forfeits because a server failed.
        ReconcileRuntime(retryDelay: TimeSpan.Zero);
        this.RegisterGrainTimer(CheckLeases, new GrainTimerCreationOptions { DueTime = _options.LeaseCheckInterval, Period = _options.LeaseCheckInterval, KeepAlive = false });
        return Task.CompletedTask;
    }

    private void Teardown()
    {
        foreach (MemberRuntime runtime in _runtime.Values)
        {
            runtime.GraceTimer?.Dispose();
        }

        _runtime.Clear();
        _connectionOwner.Clear();
        _deadlineFailures.Clear();
        foreach (string? name in _deadlineTimers.Keys.ToList())
        {
            DisarmDeadline(name);
        }

        if (_storage is null)
        {
            _memory = new RoomRecord<TState>();
        }

        DeactivateOnIdle();
    }

    private void UndoCreate()
    {
        RoomRecord<TState> record = Record;
        record.Exists = false;
        record.Incarnation = null;
        record.State = new TState();
        DeactivateOnIdle();
    }

    // ---------------------------------------------------------------- connections, presence, leases

    private void TouchGateway(GatewayRef gateway, KnownEpoch known = default)
    {
        if (!_gateways.TryGetValue(gateway.Id.Value, out GatewayLease? lease))
        {
            _gateways[gateway.Id.Value] = lease = new GatewayLease();
        }

        lease.Observer = gateway.Observer;
        lease.LastRenewal = Now;

        // The gateway has seen another epoch of this room ranked at or above this one, so it would drop this epoch's
        // batches as stale. This epoch is the live one (the gateway is talking to it), so it moves above.
        if (known.Epoch is not null && known.Epoch != _epoch && known.Order >= _epochOrder && known.Order < long.MaxValue)
        {
            _epochOrder = known.Order + 1;
        }
    }

    /// <summary>Attaches a connection to a member. Returns true when it was not attached before.</summary>
    private bool Attach(ConnectionId connection, string player, string gateway)
    {
        MemberRuntime runtime = _runtime[player];
        if (runtime.Connections.TryGetValue(connection.ClientId, out AttachedConnection existing))
        {
            if (existing.Generation > connection.Generation)
            {
                return false;
            }

            if (existing.Generation == connection.Generation && existing.Gateway == gateway)
            {
                return false;
            }
        }
        else if (_connectionOwner.TryGetValue(connection.ClientId, out string? previousOwner) && previousOwner != player && _runtime.TryGetValue(previousOwner, out MemberRuntime? previous))
        {
            previous.Connections.Remove(connection.ClientId);
            if (previous.Connections.Count == 0)
            {
                SetPresence(previousOwner, previous, PresenceStatus.Reconnecting);
            }
        }

        runtime.Connections[connection.ClientId] = new AttachedConnection(connection.Generation, gateway);
        _connectionOwner[connection.ClientId] = player;
        if (runtime.Status != PresenceStatus.Online)
        {
            SetPresence(player, runtime, PresenceStatus.Online);
        }

        return true;
    }

    /// <summary>Detaches a connection, but only the exact generation named: a late disconnect never removes a newer connection.</summary>
    private void DetachConnection(ConnectionId connection)
    {
        if (!_connectionOwner.TryGetValue(connection.ClientId, out string? player) || !_runtime.TryGetValue(player, out MemberRuntime? runtime))
        {
            return;
        }

        if (!runtime.Connections.TryGetValue(connection.ClientId, out AttachedConnection attached) || attached.Generation != connection.Generation)
        {
            return;
        }

        runtime.Connections.Remove(connection.ClientId);
        _connectionOwner.Remove(connection.ClientId);
        if (runtime.Connections.Count == 0)
        {
            SetPresence(player, runtime, PresenceStatus.Reconnecting);
        }
    }

    private void SetPresence(string player, MemberRuntime runtime, PresenceStatus status)
    {
        runtime.Status = status;
        runtime.GraceTimer?.Dispose();
        runtime.GraceTimer = null;
        if (status == PresenceStatus.Reconnecting)
        {
            StartGrace(player, runtime);
        }

        Operation op = _op!;
        op.Current.Events.Add(PresenceEvent(player, status, Record.Members.TryGetValue(player, out MemberRecord? member) ? member.Role : null));
        op.PresenceChanges.Add((player, status));
    }

    private void StartGrace(string player, MemberRuntime runtime)
    {
        runtime.GraceTimer = this.RegisterGrainTimer(() => GraceExpired(player), new GrainTimerCreationOptions { DueTime = _options.GracePeriod, Period = Timeout.InfiniteTimeSpan, KeepAlive = true });
    }

    private async Task GraceExpired(string player)
    {
        if (!_runtime.TryGetValue(player, out MemberRuntime? runtime) || runtime.Status != PresenceStatus.Reconnecting)
        {
            return;
        }

        runtime.GraceTimer?.Dispose();
        runtime.GraceTimer = null;
        Operation op = Begin();
        try
        {
            if (_info.OverridesOnPlayerLeft)
            {
                Exception? failure = await Guarded(() => OnPlayerLeft(new PlayerId(player), LeaveReason.GraceExpired));
                if (failure is not null)
                {
                    _log.LogError(failure, "OnPlayerLeft failed in room {Room}.", RoomId);
                }
            }

            op.Current.Removals.Add(new Removal(player, null, null));
            op.NeedsSave = true;
            await CommitAsync(op);
        }
        finally
        {
            End();
        }
    }

    private async Task CheckLeases()
    {
        if (_gateways.Count == 0)
        {
            return;
        }

        DateTimeOffset cutoff = Now - _options.LeaseTimeout;
        List<string> dead = [.. _gateways.Where(g => g.Value.LastRenewal < cutoff).Select(g => g.Key)];
        if (dead.Count == 0)
        {
            return;
        }

        foreach (string? gateway in dead)
        {
            _gateways.Remove(gateway);
        }

        if (!Record.Exists)
        {
            return;
        }

        Operation op = Begin();
        try
        {
            List<ConnectionId> lost = [];
            foreach (MemberRuntime runtime in _runtime.Values)
            {
                foreach ((string? clientId, AttachedConnection connection) in runtime.Connections)
                {
                    if (dead.Contains(connection.Gateway))
                    {
                        lost.Add(new ConnectionId(clientId, connection.Generation));
                    }
                }
            }

            foreach (ConnectionId connection in lost)
            {
                DetachConnection(connection);
            }

            await CommitAsync(op);
        }
        finally
        {
            End();
        }
    }

    // ---------------------------------------------------------------- deadlines

    private void ArmDeadline(string name, DateTimeOffset due) => ArmDeadlineIn(name, due - Now);

    /// <summary>
    /// A deadline whose hook failed is still due. Tries it again after a second, then after longer and longer waits
    /// up to a minute, so a hook that keeps failing does not spin.
    /// </summary>
    private void RetryDeadlineLater(string name)
    {
        // Gone with the room, or already re-armed because the state was reloaded.
        if (!Record.Exists || !Record.Deadlines.ContainsKey(name) || _deadlineTimers.ContainsKey(name))
        {
            return;
        }

        int failures = _deadlineFailures[name] = _deadlineFailures.GetValueOrDefault(name) + 1;
        ArmDeadlineIn(name, TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures - 1, 6)))));
    }

    private void ArmDeadlineIn(string name, TimeSpan delay)
    {
        DisarmDeadline(name);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        if (delay > s_maxTimerDelay)
        {
            delay = s_maxTimerDelay;
        }

        _deadlineTimers[name] = this.RegisterGrainTimer(() => DeadlineFired(name), new GrainTimerCreationOptions { DueTime = delay, Period = Timeout.InfiniteTimeSpan, KeepAlive = true });
    }

    private static readonly TimeSpan s_maxTimerDelay = TimeSpan.FromDays(30);

    private void DisarmDeadline(string name)
    {
        if (_deadlineTimers.Remove(name, out IGrainTimer? timer))
        {
            timer.Dispose();
        }
    }

    private async Task DeadlineFired(string name)
    {
        DisarmDeadline(name);
        if (!Record.Exists || !Record.Deadlines.TryGetValue(name, out DateTimeOffset due))
        {
            return;
        }

        if (due > Now + TimeSpan.FromMilliseconds(50))
        {
            ArmDeadline(name, due);
            return;
        }

        bool failed = false;
        Operation op = Begin();
        try
        {
            // Removing the deadline is part of the hook's own scope: a hook that throws changes nothing, so its
            // deadline stays and is tried again. The hook may schedule the same name again; that change comes later
            // in the scope and wins.
            Exception? failure = await Guarded(() =>
            {
                CurrentScope().DeadlineChanges.Add((name, null));
                return OnDeadline(name);
            });
            if (failure is not null)
            {
                _log.LogError(failure, "OnDeadline('{Deadline}') failed in room {Room}; it will be tried again.", name, RoomId);
                failed = true;
            }
            else
            {
                op.NeedsSave = true;
            }

            await CommitAsync(op);
        }
        finally
        {
            End();
        }

        if (failed)
        {
            RetryDeadlineLater(name);
        }
    }

    // ---------------------------------------------------------------- de-duplication

    private bool TryGetRecorded(string player, string commandId, [NotNullWhen(true)] out RoomReply? reply)
    {
        if (Record.Dedup.TryGetValue(player, out List<DedupEntry>? entries))
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].CommandId == commandId)
                {
                    reply = entries[i].Reply;
                    return true;
                }
            }
        }

        reply = null;
        return false;
    }

    private void Remember(string player, string commandId, RoomReply reply)
    {
        if (!Record.Dedup.TryGetValue(player, out List<DedupEntry>? entries))
        {
            Record.Dedup[player] = entries = [];
        }

        entries.Add(new DedupEntry { CommandId = commandId, At = Now, Reply = reply.WithoutSnapshot() });

        // Keep the newest DedupCapacity entries, and anything younger than DedupWindow, whichever is more.
        int excess = entries.Count - _options.DedupCapacity;
        if (excess <= 0)
        {
            return;
        }

        DateTimeOffset cutoff = Now - _options.DedupWindow;
        int hardExcess = entries.Count - (_options.DedupCapacity * 16);
        int remove = 0;
        while (remove < excess && (entries[remove].At < cutoff || remove < hardExcess))
        {
            remove++;
        }

        if (remove > 0)
        {
            entries.RemoveRange(0, remove);
        }
    }

    // ---------------------------------------------------------------- helpers

    private RoomPayload Payload(byte[]? bytes) => new(bytes, _serializer);

    private RoomMember ToRoomMember(MemberRecord member) =>
        new(new PlayerId(member.Player), member.Role, _runtime.TryGetValue(member.Player, out MemberRuntime? runtime) ? runtime.Status : PresenceStatus.Reconnecting);

    private SnapshotData BuildSnapshot(string player)
    {
        RoomRecord<TState> record = Record;
        List<MemberInfo> members = new(record.Members.Count);
        MemberInfo? you = null;
        foreach (MemberRecord member in record.Members.Values)
        {
            MemberInfo info = new()
            {
                Player = member.Player,
                Role = member.Role,
                Status = _runtime.TryGetValue(member.Player, out MemberRuntime? runtime) ? runtime.Status : PresenceStatus.Reconnecting,
            };
            members.Add(info);
            if (member.Player == player)
            {
                you = info;
            }
        }

        return new SnapshotData
        {
            Incarnation = record.Incarnation ?? "",
            Epoch = _epoch,
            At = _runtime[player].Seq,
            View = _serializer.Serialize(GetSnapshot(new PlayerId(player))),
            Members = members,
            You = you!,
        };
    }

    private RoomReply FailureReply(Exception failure, string rejectionCode, string what)
    {
        if (failure is RoomRejectedException rejected)
        {
            return RoomReply.Rejected(rejected.Code ?? rejectionCode, rejected.Message);
        }

        _log.LogError(failure, "{What} failed in room {RoomType}/{Room}.", what, RoomType, RoomId);
        return RoomReply.Error(Wire.Codes.HandlerError, "The room could not process this request.");
    }

    private OutEvent PresenceEvent(string player, PresenceStatus status, string? role, bool includeSelf = false) => new()
    {
        Type = Wire.Types.Presence,
        Data = FrameworkJson.Serialize(new PresenceNotice(player, StatusName(status), role)),
        Except = includeSelf ? null : player,
    };

    private OutEvent ClosedNotice(string reason, string? message, List<(string Gateway, string ClientId)> targets) => new()
    {
        Type = Wire.Types.Closed,
        Data = FrameworkJson.Serialize(new ClosedNotice(reason, message)),
        Sequenced = false,
        Targets = targets,
    };

    private static string StatusName(PresenceStatus status) => status switch
    {
        PresenceStatus.Online => "online",
        PresenceStatus.Reconnecting => "reconnecting",
        _ => "left",
    };

    private static string EventName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '$')
        {
            throw new ArgumentException("Event names must not be empty or start with '$'.", nameof(name));
        }

        return name;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed class Operation
    {
        public Scope Current = new();
        public readonly List<(string Player, PresenceStatus Status)> PresenceChanges = [];
        public bool NeedsSave;
        public bool Poisoned;
    }

    /// <summary>What app code asked for during one guarded call; merged into the operation only if the call succeeds.</summary>
    private sealed class Scope
    {
        public readonly List<OutEvent> Events = [];
        public readonly List<(string Player, string? Role)> RoleChanges = [];
        public readonly List<Removal> Removals = [];
        public readonly List<(string Name, DateTimeOffset? Due)> DeadlineChanges = [];
        public bool Close;
        public string? CloseMessage;

        public void MergeInto(Scope target)
        {
            target.Events.AddRange(Events);
            target.RoleChanges.AddRange(RoleChanges);
            target.Removals.AddRange(Removals);
            target.DeadlineChanges.AddRange(DeadlineChanges);
            if (Close)
            {
                target.Close = true;
                target.CloseMessage = CloseMessage;
            }
        }
    }

    private sealed class OutEvent
    {
        public required string Type;
        public byte[]? Data;
        public string? Only;
        public string? Except;
        public bool Sequenced = true;
        public List<(string Gateway, string ClientId)>? Targets;
    }

    private sealed record Removal(string Player, string? ClosedReason, string? Message);

    private sealed class MemberRuntime
    {
        public long Seq;
        public PresenceStatus Status;
        public IGrainTimer? GraceTimer;
        public readonly Dictionary<string, AttachedConnection> Connections = new(StringComparer.Ordinal);
    }

    private readonly record struct AttachedConnection(long Generation, string Gateway);

    private sealed class GatewayLease
    {
        public IGatewayObserver Observer = null!;
        public DateTimeOffset LastRenewal;
    }
}
