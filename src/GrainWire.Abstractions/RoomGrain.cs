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
/// <para>
/// Everything a handler sends (<see cref="Broadcast"/>, <see cref="SendTo"/>, <see cref="Kick"/> ...) is held back until
/// the command has committed. If the handler calls <see cref="Reject"/> or throws, the state is restored and nothing is sent.
/// </para>
/// <para>
/// How the class is put together:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Durable vs. runtime data.</b> <see cref="RoomRecord{TState}"/> (reached through <c>Record</c>) holds what survives a
/// restart when the room has storage: the app state, members and roles, deadlines and the de-duplication log. Presence,
/// attached connections, sequence numbers, timers and gateway leases live only in the activation and are rebuilt by
/// <c>ReconcileRuntime</c>.
/// </description></item>
/// <item><description>
/// <b>Operations.</b> Every entry point (a gateway call, a timer, <see cref="ExecuteAsync"/>) runs as one
/// <c>Operation</c>: <c>Begin</c> → work → <c>CommitAsync</c> → <c>End</c>. Orleans runs one turn at a time, so at most
/// one operation exists and <c>_op</c> points at it.
/// </description></item>
/// <item><description>
/// <b>Scopes.</b> App code (handlers and hooks) runs through <c>Guarded</c>, which gives it a fresh <c>Scope</c> (an outbox
/// of events, role changes, removals, deadline changes and a close flag) and a way back to the old state. The scope is
/// merged into the operation only if the code succeeds.
/// </description></item>
/// <item><description>
/// <b>Commit, then publish.</b> <c>CommitAsync</c> applies the scope to the record, saves it, and only after a
/// successful save stamps the events with sequence numbers and pushes them to gateways.
/// </description></item>
/// <item><description>
/// <b>Incarnation and epoch.</b> The incarnation is a new id each time the room is created, so a client can tell a
/// closed-and-recreated room from the one it knew. The epoch is a new id on every activation; per-member sequence
/// numbers restart with it, so a client's position is (incarnation, epoch, seq).
/// </description></item>
/// </list>
/// </remarks>
/// <typeparam name="TState">
/// The type of the room's state. It must be a class with a parameterless constructor, and it must be serializable
/// when the room type has a storage provider.
/// </typeparam>
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
    // Exactly one of these holds the record: _storage when the room type has a storage provider, else _memory.
    private readonly IPersistentState<RoomRecord<TState>>? _storage;
    private RoomRecord<TState> _memory = new();

    // Not durable, by design: a new stream epoch and fresh presence on every activation.
    private readonly string _epoch = NewId();

    // Ranks this epoch against the room's other epochs for gateways (see DeliveryBatch.EpochOrder). It starts from the
    // clock, and is raised when a gateway reports an epoch ranked at or above it, because clocks differ between silos.
    private long _epochOrder;

    // Non-durable state of this activation. All keyed by plain strings (player id, client id, gateway id, deadline
    // name) with case-sensitive ordinal comparison.

    // Per member: presence, stream position, grace timer and attached connections.
    private readonly Dictionary<string, MemberRuntime> _runtime = new(StringComparer.Ordinal);

    // Reverse index: MQTT client id -> the player it is attached as.
    private readonly Dictionary<string, string> _connectionOwner = new(StringComparer.Ordinal);

    // Gateways that have talked to this room recently: where to deliver events and when they last checked in.
    private readonly Dictionary<string, GatewayLease> _gateways = new(StringComparer.Ordinal);

    // Armed Orleans timers for the deadlines in Record.Deadlines, and how often each one's hook has failed in a row.
    private readonly Dictionary<string, IGrainTimer> _deadlineTimers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deadlineFailures = new(StringComparer.Ordinal);

    // Last batch number sent to each gateway (see Send).
    private readonly Dictionary<string, long> _batchNumbers = new(StringComparer.Ordinal);

    // The operation currently running, or null between operations.
    private Operation? _op;

    /// <summary>
    /// Creates the room grain: reads the room type's <see cref="RoomOptions"/>, the serializer and the clock from the
    /// activation services, and sets up storage when the room type has a storage provider.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The class has no <see cref="RoomTypeAttribute"/>, is reentrant, or has an invalid or duplicate command handler.
    /// </exception>
    protected RoomGrain()
    {
        // Reflection data is cached per class; this also throws if the class is not a valid room grain.
        _info = RoomTypeInfo.For(GetType());

        // A parameterless constructor keeps derived rooms simple, so services come from the activation instead of
        // constructor injection. Options are named by room type, so each room type can be configured separately.
        IServiceProvider services = GrainContext.ActivationServices;
        _options = services.GetService<IOptionsMonitor<RoomOptions>>()?.Get(_info.RoomType) ?? new RoomOptions();
        _serializer = services.GetService<IRoomSerializer>() ?? new JsonRoomSerializer();
        _copier = services.GetRequiredService<DeepCopier>();
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _epochOrder = _time.GetUtcNow().UtcTicks;
        _log = services.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        if (_options.StorageProvider is { } provider)
        {
            // Created by hand rather than with [PersistentState] because the provider name comes from options.
            _storage = services.GetRequiredService<IPersistentStateFactory>()
                .Create<RoomRecord<TState>>(GrainContext, new PersistentStateAttribute(StateName, provider));
        }

        // Runs after stored state is loaded and before OnActivateAsync.
        GrainContext.ObservableLifecycle.Subscribe(GetType().FullName!, GrainLifecycleStage.SetupState + 1,
            OnRoomActivating);
    }

    // ---------------------------------------------------------------- what handlers can use

    /// <summary>
    /// The room's state. Changes made by a handler are kept only if the command commits.
    /// </summary>
    protected TState State
    {
        get => Record.State;
        set => Record.State = value;
    }

    /// <summary>
    /// The room type name given to <see cref="RoomTypeAttribute"/>.
    /// </summary>
    protected string RoomType => _info.RoomType;

    /// <summary>
    /// The id of this room (the second topic segment after <c>rooms/</c>).
    /// </summary>
    protected string RoomId => this.GetPrimaryKeyString();

    /// <summary>
    /// The current UTC time from the registered <see cref="TimeProvider"/>. Use it instead of the system clock so tests can control time.
    /// </summary>
    protected DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// The members of the room with their roles and presence.
    /// </summary>
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

    /// <summary>
    /// Finds one member of the room.
    /// </summary>
    /// <param name="player">The player to look for.</param>
    /// <returns>The member, or <see langword="null"/> when the player is not a member.</returns>
    protected RoomMember? FindMember(PlayerId player) =>
        Record.Members.TryGetValue(player.Value, out MemberRecord? member) ? ToRoomMember(member) : null;

    /// <summary>
    /// Decides whether <see cref="CreateContext.Creator"/> may create this room. Default: allow.
    /// </summary>
    /// <param name="context">Who is creating the room and the settings they chose.</param>
    /// <returns>Whether the room may be created.</returns>
    protected virtual Task<CreateResult> OnCreate(CreateContext context) => Task.FromResult(CreateResult.Allow());

    /// <summary>
    /// Decides whether a new player may join, and with which role. Default: accept without a role.
    /// </summary>
    /// <param name="context">Who is joining and the join data they sent.</param>
    /// <returns>Whether the player is accepted, and with which role.</returns>
    protected virtual Task<JoinResult> OnJoin(JoinContext context) => Task.FromResult(JoinResult.Accept());

    /// <summary>
    /// Re-checks access when an existing member comes back (bans, revoked invites). The role is kept. Default: accept.
    /// </summary>
    /// <param name="context">Who is rejoining and the join data they sent.</param>
    /// <returns>Whether the member may come back.</returns>
    protected virtual Task<JoinResult> OnRejoin(JoinContext context) => Task.FromResult(JoinResult.Accept());

    /// <summary>
    /// Called when a member goes online or starts reconnecting. Other members are told automatically.
    /// </summary>
    /// <param name="player">The member whose presence changed.</param>
    /// <param name="status">The new presence.</param>
    protected virtual Task OnPresenceChanged(PlayerId player, PresenceStatus status) => Task.CompletedTask;

    /// <summary>
    /// Called when a member's grace period ran out, just before they are removed.
    /// </summary>
    /// <param name="player">The member who is leaving.</param>
    /// <param name="reason">Why the member is leaving.</param>
    protected virtual Task OnPlayerLeft(PlayerId player, LeaveReason reason) => Task.CompletedTask;

    /// <summary>
    /// Called when a deadline set with <see cref="Schedule(string, DateTimeOffset)"/> is due.
    /// </summary>
    /// <param name="name">The name the deadline was scheduled with.</param>
    protected virtual Task OnDeadline(string name) => Task.CompletedTask;

    /// <summary>
    /// What <paramref name="viewer"/> is allowed to see of the room. Default: the whole state.
    /// </summary>
    /// <param name="viewer">The player the snapshot is built for.</param>
    /// <returns>The object sent to the viewer as the room view, or <see langword="null"/> for none.</returns>
    protected virtual object? GetSnapshot(PlayerId viewer) => State;

    /// <summary>
    /// Sends an event to every member (optionally all but one) once the current command has committed.
    /// </summary>
    /// <param name="name">The event name. It must be a valid segment and may not start with a dollar sign.</param>
    /// <param name="data">The event payload, or <see langword="null"/> for none.</param>
    /// <param name="except">A member who does not get the event, for example the sender.</param>
    /// <exception cref="InvalidOperationException">Called outside a command, hook or <see cref="ExecuteAsync"/>.</exception>
    protected void Broadcast(string name, object? data = null, PlayerId? except = null)
    {
        CurrentScope().Events.Add(new OutEvent
        {
            Type = EventName(name), Data = _serializer.Serialize(data), Except = except?.Value
        });
    }

    /// <summary>
    /// Sends an event to one member once the current command has committed.
    /// </summary>
    /// <param name="player">The member who gets the event.</param>
    /// <param name="name">The event name. It must be a valid segment and may not start with a dollar sign.</param>
    /// <param name="data">The event payload, or <see langword="null"/> for none.</param>
    /// <exception cref="InvalidOperationException">Called outside a command, hook or <see cref="ExecuteAsync"/>.</exception>
    protected void SendTo(PlayerId player, string name, object? data = null)
    {
        CurrentScope().Events
            .Add(new OutEvent { Type = EventName(name), Data = _serializer.Serialize(data), Only = player.Value });
    }

    /// <summary>
    /// Ends the current command with a rejection. Nothing the handler did is kept or sent.
    /// </summary>
    /// <param name="reason">A message for the sender.</param>
    /// <param name="code">An optional machine-readable code the client can act on.</param>
    /// <exception cref="RoomRejectedException">Always thrown.</exception>
    [DoesNotReturn]
    protected void Reject(string reason, string? code = null) => throw new RoomRejectedException(reason, code);

    /// <summary>
    /// Changes a member's role when the current command commits.
    /// </summary>
    /// <param name="player">The member whose role changes.</param>
    /// <param name="role">The new role, or <see langword="null"/> for none.</param>
    protected void SetRole(PlayerId player, string? role) => CurrentScope().RoleChanges.Add((player.Value, role));

    /// <summary>
    /// Removes a member when the current command commits and tells their connections why.
    /// </summary>
    /// <param name="player">The member to remove.</param>
    /// <param name="reason">A message shown to the removed member.</param>
    protected void Kick(PlayerId player, string? reason = null) =>
        CurrentScope().Removals.Add(new Removal(player.Value, Wire.ClosedReasons.Kicked, reason));

    /// <summary>
    /// Removes a member when the current command commits (for example because they resigned their seat).
    /// </summary>
    /// <param name="player">The member to remove.</param>
    protected void RemoveMember(PlayerId player) =>
        CurrentScope().Removals.Add(new Removal(player.Value, Wire.ClosedReasons.Removed, null));

    /// <summary>
    /// Closes the room for everyone and deletes it when the current command commits.
    /// </summary>
    /// <param name="reason">A message shown to every member.</param>
    protected void CloseRoom(string? reason = null)
    {
        Scope scope = CurrentScope();
        scope.Close = true;
        scope.CloseMessage = reason;
    }

    /// <summary>
    /// Sets (or moves) a named deadline. It survives restarts when the room is persistent. <see cref="OnDeadline"/> runs when it is due.
    /// </summary>
    /// <param name="name">The deadline's name. Scheduling the same name again moves it.</param>
    /// <param name="dueAt">When the deadline is due.</param>
    protected void Schedule(string name, DateTimeOffset dueAt) => CurrentScope().DeadlineChanges.Add((name, dueAt));

    /// <summary>
    /// Sets (or moves) a named deadline that falls due after a delay from <see cref="Now"/>.
    /// </summary>
    /// <param name="name">The deadline's name. Scheduling the same name again moves it.</param>
    /// <param name="delay">How long from now the deadline is due.</param>
    protected void Schedule(string name, TimeSpan delay) => Schedule(name, Now + delay);

    /// <summary>
    /// Cancels a named deadline when the current command commits. Does nothing if there is no such deadline.
    /// </summary>
    /// <param name="name">The name the deadline was scheduled with.</param>
    protected void CancelSchedule(string name) => CurrentScope().DeadlineChanges.Add((name, null));

    /// <summary>
    /// Runs <paramref name="action"/> as one committed room operation. Use it from your own grain methods or timers so that
    /// state changes and events get the same commit-then-publish treatment as command handlers.
    /// </summary>
    /// <param name="action">The work to run. It may change <see cref="State"/> and send events.</param>
    /// <returns>A task that completes once the operation has committed.</returns>
    /// <exception cref="RoomNotCommittedException">The change could not be saved and was rolled back.</exception>
    protected async Task ExecuteAsync(Func<Task> action)
    {
        // Already inside an operation (called from a handler or hook): the action simply becomes part of it and is
        // committed or rolled back together with the code that called it.
        if (_op is not null)
        {
            await action();
            return;
        }

        // Otherwise (a custom grain method or timer) it gets its own operation, and failures surface to the caller.
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

    /// <inheritdoc />
    async Task<RoomReply> IRoomGrain.Join(JoinRequest request)
    {
        // Every gateway call renews that gateway's lease and refreshes its observer reference.
        TouchGateway(request.Gateway, request.Known);
        string player = request.Player.Value;
        IReadOnlyDictionary<string, string> claims = (IReadOnlyDictionary<string, string>?)request.Claims ?? s_noClaims;
        Operation op = Begin();
        try
        {
            bool created = false;
            if (!Record.Exists)
            {
                // Calling a grain always activates it, so a missing room shows up as an empty record. Deactivate
                // rather than keep an empty activation around.
                if (!request.Create)
                {
                    DeactivateOnIdle();
                    return RoomReply.Error(Wire.Codes.RoomNotFound);
                }

                CreateResult? creation = null;
                Exception? createFailure = await Guarded(async () =>
                    creation = await OnCreate(new CreateContext(request.Player, claims, Payload(request.Options))));
                if (createFailure is not null || creation is not { Allowed: true })
                {
                    DeactivateOnIdle();
                    return createFailure is not null
                        ? FailureReply(createFailure, Wire.Codes.CreateDenied, nameof(OnCreate))
                        : RoomReply.Rejected(Wire.Codes.CreateDenied, creation?.Reason);
                }

                // The room exists in memory from here on, but is only saved together with the creator's join below.
                // A new incarnation id tells clients this is not an earlier room that had the same id.
                Record.Exists = true;
                Record.Incarnation = NewId();
                Record.CreatedAt = Now;
                created = true;
                op.NeedsSave = true;
            }

            JoinContext context = new(request.Player, claims, Payload(request.Data), created);
            if (Record.Members.ContainsKey(player))
            {
                // Already a member (a reconnect, another tab, or a join after a restart). The role is kept; the room
                // is only asked again if it overrides OnRejoin, otherwise the member is simply let back in.
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
                // A new member: the capacity check comes first so OnJoin never sees a join that could not succeed.
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
                    // A room whose creator may not join is never created: nothing was saved, so just forget it.
                    if (created)
                    {
                        UndoCreate();
                    }

                    return refusal;
                }

                Record.Members[player] = new MemberRecord { Player = player, Role = join!.Role, JoinedAt = Now };
                _runtime[player] = new MemberRuntime { Status = PresenceStatus.Online };

                // Tells the other members (not the joiner) that someone arrived.
                op.Current.Events.Add(PresenceEvent(player, PresenceStatus.Online, join.Role));
                op.NeedsSave = true;
            }

            // Bind this connection to the member so it receives the room's events from now on.
            Attach(request.Connection, player, request.Gateway.Id.Value);
            if (!await CommitAsync(op))
            {
                return RoomReply.Error(Wire.Codes.NotCommitted);
            }

            // The joiner gets a full snapshot; its stream position (At) is where the events it receives next continue.
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

    /// <inheritdoc />
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
            // A command can be the first thing the room hears from a connection (for example after the room was
            // reactivated), so attach it here too. Attaching happens outside the handler's scope: a rejected
            // command does not undo it.
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
                // The handler's scope is already dropped, but the operation still commits: the attach above may have
                // changed presence, and those events must still go out. A failure is not recorded for de-duplication,
                // so a retry runs the handler again.
                reply = FailureReply(failure, Wire.Codes.Rejected, request.Name);
                await CommitAsync(op);
            }
            else
            {
                reply = RoomReply.Ok(data: data);
                reply.Incarnation = Record.Incarnation;
                op.NeedsSave = true;

                // Recorded before the commit so the de-duplication entry is saved in the same write as the change.
                if (request.CommandId is not null)
                {
                    Remember(player, request.CommandId, reply);
                }

                if (!await CommitAsync(op))
                {
                    reply = RoomReply.Error(Wire.Codes.NotCommitted);
                }
            }

            // A connection attached just now has no starting point for the event stream, so the reply carries a
            // snapshot. Skipped if the command removed the sender or closed the room.
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

    /// <inheritdoc />
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
            // Commit only publishes the presence change, if attaching caused one; there is nothing to save.
            bool attached = Attach(request.Connection, player, request.Gateway.Id.Value);
            await CommitAsync(op);

            // The client is in sync only if it was already attached and its position (incarnation, epoch, seq)
            // is exactly the member's current one. Anything else (a gap, a new epoch after a restart, a recreated
            // room) gets a fresh snapshot.
            long position = _runtime[player].Seq;
            if (!attached && request.Incarnation == Record.Incarnation && request.Epoch == _epoch &&
                request.Seq == position)
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

    /// <inheritdoc />
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
            // Only this connection is detached; membership is not removed here. If it was the member's last
            // connection, they turn "reconnecting" and are removed when the grace period runs out.
            DetachConnection(request.Connection);
            await CommitAsync(op);
            return RoomReply.Ok(Wire.Codes.Left);
        }
        finally
        {
            End();
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    async Task<RenewReply> IRoomGrain.Renew(RenewRequest request)
    {
        RenewReply reply = new() { RoomExists = Record.Exists };
        if (!Record.Exists)
        {
            DeactivateOnIdle();
            return reply;
        }

        // The gateway's periodic check-in. Besides keeping its lease alive, it lists every connection it holds for
        // this room, which lets a freshly reactivated room learn who is connected without waiting for each client.
        TouchGateway(request.Gateway, request.Known);
        string gateway = request.Gateway.Id.Value;
        Operation op = Begin();
        try
        {
            // Connections whose player is no longer a member are reported back so the gateway can drop them;
            // connections the room did not know yet are attached and get a snapshot.
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
                    // Re-checked after the commit: a presence hook may have removed the member meanwhile.
                    if (Record.Members.ContainsKey(player))
                    {
                        reply.Snapshots.Add(new ConnectionSnapshot
                        {
                            Connection = connection, Snapshot = BuildSnapshot(player)
                        });
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

    /// <summary>
    /// Starts an operation. Orleans runs a non-reentrant grain one turn at a time (timer callbacks included), so a
    /// second operation can only appear if the class was made reentrant despite <see cref="RoomTypeInfo"/>'s check.
    /// </summary>
    private Operation Begin()
    {
        if (_op is not null)
        {
            throw new InvalidOperationException(
                "A room operation is already running. Room grains must not be reentrant.");
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
        // Swap in a fresh scope so everything the app code asks for lands there, not in the operation directly.
        // Guarded calls can nest (a hook run during CommitAsync, ExecuteAsync inside a handler); each level merges
        // into the one around it.
        Operation op = _op!;
        Scope outer = op.Current;
        Scope scope = new();
        op.Current = scope;

        // RollbackMode.Copy: deep-copy the state up front so a failure can restore it exactly. Costs a copy per
        // call; RollbackMode.None skips it and handles failure below instead.
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
                    // Reloading also throws away whatever this operation changed before the failure, so the
                    // operation as a whole must not commit.
                    await ReloadAsync();
                    op.Poisoned = true;
                }
                else
                {
                    _log.LogError(ex,
                        "Room {Room} uses RollbackMode.None and a handler threw: its state may be inconsistent.",
                        RoomId);
                }
            }

            // The failed scope is not merged, so its events, role changes, removals and deadlines are dropped.
            return ex;
        }
        finally
        {
            op.Current = outer;
        }
    }

    /// <summary>
    /// Applies what the operation decided, saves it, and only then lets events out.
    /// </summary>
    private async Task<bool> CommitAsync(Operation op)
    {
        if (op.Poisoned)
        {
            return false;
        }

        // Presence hooks run first, as part of this same operation, so whatever they change or send is committed
        // together with the operation that caused the presence change. A failing hook is logged and skipped; it
        // does not fail the operation.
        if (_info.OverridesOnPresenceChanged && op.PresenceChanges.Count > 0)
        {
            // ToArray: iterate over a copy so the loop is safe even if something adds to the list meanwhile.
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

        // Apply the outbox's durable changes to the record. Nothing here is visible to clients yet.
        Scope scope = op.Current;
        RoomRecord<TState> record = Record;

        // Role changes. includeSelf: true — unlike other presence events, the member also needs to learn their new role.
        foreach ((string? player, string? role) in scope.RoleChanges)
        {
            if (!record.Members.TryGetValue(player, out MemberRecord? member) || member.Role == role)
            {
                continue;
            }

            member.Role = role;
            scope.Events.Add(PresenceEvent(player,
                _runtime.TryGetValue(player, out MemberRuntime? rt) ? rt.Status : PresenceStatus.Reconnecting, role,
                includeSelf: true));
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

        // Removals. Only the durable part happens now; tearing down their connections and telling others waits
        // until after the save. Duplicate or unknown removals are skipped.
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

        // Save. Closing the room deletes its stored record instead. In-memory rooms have nothing to save, and
        // operations that only touched non-durable data (presence, attaches) skip the write.
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
                // The record in memory now holds changes that are not in storage. Reload to get back to the last
                // saved state; nothing from this operation has been sent, so clients never saw it.
                _log.LogError(ex, "Room {Room} could not save; the operation was rolled back.", RoomId);
                await ReloadAsync();
                return false;
            }
        }

        // Committed. From here on nothing can fail the operation.

        // Timers follow the saved deadlines. A deadline that was set or cancelled starts over with no failure count.
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

        // The handler's events were stamped above while removed members were still in _runtime, so they still get
        // them. Now drop the removed members; the follow-up notices below are stamped without them.
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

                // Kicked/removed members are told why on each of their connections. The notice is addressed to the
                // connections directly (they are no longer members, so they have no stream position). A member whose
                // grace ran out has no connections and no ClosedReason.
                if (removal.ClosedReason is not null && runtime.Connections.Count > 0)
                {
                    followUp.Add(ClosedNotice(removal.ClosedReason, removal.Message,
                        [.. runtime.Connections.Select(c => (c.Value.Gateway, c.Key))]));
                }

                foreach (string clientId in runtime.Connections.Keys)
                {
                    _connectionOwner.Remove(clientId);
                }

                // The remaining members learn that this one left.
                followUp.Add(PresenceEvent(removal.Player, PresenceStatus.Left, null));
            }

            Stamp(followUp, batches);
        }

        // Closing: every connection still attached gets a final "closed" notice in the same batches, then the
        // activation drops its runtime state and deactivates.
        if (scope.Close)
        {
            List<(string Gateway, string Key)> everyone =
                [.. _runtime.Values.SelectMany(r => r.Connections.Select(c => (c.Value.Gateway, c.Key)))];
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

    /// <summary>
    /// Gives each event its per-player sequence numbers and sorts it into the batch of each gateway involved.
    /// </summary>
    private void Stamp(List<OutEvent> events, Dictionary<string, DeliveryBatch> batches)
    {
        foreach (OutEvent outgoing in events)
        {
            // One DeliveryItem per gateway per event: the payload is sent to each gateway once, with a list of the
            // connections there that receive it and the sequence number each of them sees.
            Dictionary<string, DeliveryItem>? items = null;

            void Add(string gateway, string clientId, long seq)
            {
                items ??= new(StringComparer.Ordinal);
                if (!items.TryGetValue(gateway, out DeliveryItem? item))
                {
                    item = new DeliveryItem
                    {
                        Type = outgoing.Type, Data = outgoing.Data, Sequenced = outgoing.Sequenced
                    };
                    items[gateway] = item;
                    if (!batches.TryGetValue(gateway, out DeliveryBatch? batch))
                    {
                        batch = new DeliveryBatch
                        {
                            Room = $"{RoomType}/{RoomId}", Incarnation = Record.Incarnation ?? "", Epoch = _epoch
                        };
                        batches[gateway] = batch;
                    }

                    batch.Items.Add(item);
                }

                item.Targets.Add(new DeliveryTarget { ClientId = clientId, Seq = seq });
            }

            // Three ways to address an event:
            // 1. Explicit connections (closed notices): no member stream is involved, so seq 0 and no position moves.
            if (outgoing.Targets is not null)
            {
                foreach ((string? gateway, string? clientId) in outgoing.Targets)
                {
                    Add(gateway, clientId, 0);
                }

                continue;
            }

            // 2. One member (SendTo).
            if (outgoing.Only is not null)
            {
                if (_runtime.TryGetValue(outgoing.Only, out MemberRuntime? only))
                {
                    Advance(only);
                }

                continue;
            }

            // 3. Every member, optionally minus one (Broadcast, presence events).
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
            // The gateway's lease expired (CheckLeases removed it); its connections are being detached anyway.
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

    /// <summary>
    /// Replaces the in-memory record with the last saved one, then realigns presence and timers with it.
    /// </summary>
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

        // Deadlines that are already due wait a second instead of firing at once, so a reload caused by a failing
        // deadline hook cannot turn into a tight retry loop.
        ReconcileRuntime(retryDelay: TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Brings the non-durable parts (presence, timers) back in line with the record.
    /// </summary>
    private void ReconcileRuntime(TimeSpan retryDelay)
    {
        RoomRecord<TState> record = Record;

        // 1. Forget runtime data of players who are not members in the record (or of a room that no longer exists).
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

        // 2. Drop every deadline timer; they are re-armed from the record below.
        foreach (string? name in _deadlineTimers.Keys.ToList())
        {
            DisarmDeadline(name);
        }

        if (!record.Exists)
        {
            return;
        }

        // 3. Every member needs runtime data. One with no attached connection is "reconnecting" and gets a grace
        //    period; existing grace timers are left running rather than restarted.
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

        // 4. Arm a timer per saved deadline. Ones already overdue fire after retryDelay.
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

        // Periodically drops gateways that stopped renewing. KeepAlive = false: this timer alone does not keep an
        // idle room activated.
        this.RegisterGrainTimer(CheckLeases,
            new GrainTimerCreationOptions
            {
                DueTime = _options.LeaseCheckInterval, Period = _options.LeaseCheckInterval, KeepAlive = false
            });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends the room in this activation: stops all timers, forgets presence and connections, clears an in-memory
    /// record, and deactivates. Stored state is cleared by <c>CommitAsync</c>, not here.
    /// </summary>
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

    /// <summary>
    /// Reverts a creation that was refused before it was saved, so the activation is back to "room does not exist".
    /// </summary>
    private void UndoCreate()
    {
        RoomRecord<TState> record = Record;
        record.Exists = false;
        record.Incarnation = null;
        record.State = new TState();
        DeactivateOnIdle();
    }

    // ---------------------------------------------------------------- connections, presence, leases

    /// <summary>
    /// Records that <paramref name="gateway"/> is alive, keeps its latest observer reference for delivery, and adjusts
    /// this epoch's rank if the gateway has already seen a higher one.
    /// </summary>
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
        if (known.Epoch is not null && known.Epoch != _epoch && known.Order >= _epochOrder &&
            known.Order < long.MaxValue)
        {
            _epochOrder = known.Order + 1;
        }
    }

    /// <summary>
    /// Attaches a connection to a member. Returns true when it was not attached before.
    /// </summary>
    private bool Attach(ConnectionId connection, string player, string gateway)
    {
        MemberRuntime runtime = _runtime[player];
        if (runtime.Connections.TryGetValue(connection.ClientId, out AttachedConnection existing))
        {
            // A request from an older generation of this client id arrived late: the newer connection stays.
            if (existing.Generation > connection.Generation)
            {
                return false;
            }

            // Same connection, same gateway: already attached, nothing to do. (Same generation through a different
            // gateway falls through and is treated as a fresh attach, so the reply carries a snapshot.)
            if (existing.Generation == connection.Generation && existing.Gateway == gateway)
            {
                return false;
            }
        }
        else if (_connectionOwner.TryGetValue(connection.ClientId, out string? previousOwner) &&
                 previousOwner != player && _runtime.TryGetValue(previousOwner, out MemberRuntime? previous))
        {
            // The client id was attached as a different player (for example, the tab signed in as someone else).
            // A client id belongs to one player at a time, so take it away from the previous one.
            previous.Connections.Remove(connection.ClientId);
            if (previous.Connections.Count == 0)
            {
                SetPresence(previousOwner, previous, PresenceStatus.Reconnecting);
            }
        }

        // Store (or replace with the newer generation / new gateway) and mark the member online if they were not.
        runtime.Connections[connection.ClientId] = new AttachedConnection(connection.Generation, gateway);
        _connectionOwner[connection.ClientId] = player;
        if (runtime.Status != PresenceStatus.Online)
        {
            SetPresence(player, runtime, PresenceStatus.Online);
        }

        return true;
    }

    /// <summary>
    /// Detaches a connection, but only the exact generation named: a late disconnect never removes a newer connection.
    /// </summary>
    private void DetachConnection(ConnectionId connection)
    {
        if (!_connectionOwner.TryGetValue(connection.ClientId, out string? player) ||
            !_runtime.TryGetValue(player, out MemberRuntime? runtime))
        {
            return;
        }

        if (!runtime.Connections.TryGetValue(connection.ClientId, out AttachedConnection attached) ||
            attached.Generation != connection.Generation)
        {
            return;
        }

        runtime.Connections.Remove(connection.ClientId);
        _connectionOwner.Remove(connection.ClientId);

        // A member with another tab still attached stays online; only the last connection going starts the grace period.
        if (runtime.Connections.Count == 0)
        {
            SetPresence(player, runtime, PresenceStatus.Reconnecting);
        }
    }

    /// <summary>
    /// Changes a member's presence: (re)starts or stops the grace timer, queues a presence event for the other members,
    /// and records the change so <c>CommitAsync</c> can run <see cref="OnPresenceChanged"/>. Must run inside an operation.
    /// </summary>
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
        op.Current.Events.Add(PresenceEvent(player, status,
            Record.Members.TryGetValue(player, out MemberRecord? member) ? member.Role : null));
        op.PresenceChanges.Add((player, status));
    }

    /// <summary>
    /// Gives a disconnected member <see cref="RoomOptions.GracePeriod"/> to come back before they are removed.
    /// </summary>
    private void StartGrace(string player, MemberRuntime runtime)
    {
        // One-shot timer. KeepAlive = true: the room must stay activated for the grace period to run out, otherwise an
        // idle room would deactivate and the member would never be removed.
        runtime.GraceTimer = this.RegisterGrainTimer(() => GraceExpired(player),
            new GrainTimerCreationOptions
            {
                DueTime = _options.GracePeriod, Period = Timeout.InfiniteTimeSpan, KeepAlive = true
            });
    }

    private async Task GraceExpired(string player)
    {
        // The member came back, or was removed some other way, after the timer was already queued.
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

            // The member is removed even if the hook failed: a buggy hook must not keep a departed player in the room.
            // No closed reason, since there is no connection left to tell.
            op.Current.Removals.Add(new Removal(player, null, null));
            op.NeedsSave = true;
            await CommitAsync(op);
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Treats a gateway that has not called for <see cref="RoomOptions.LeaseTimeout"/> as gone (crashed or cut off)
    /// and detaches its connections, so their members go "reconnecting" instead of looking online forever.
    /// </summary>
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
    //
    // Record.Deadlines (durable) is the truth; _deadlineTimers are one-shot Orleans timers that mirror it in this
    // activation. Timers are re-created from the record on activation and after a reload.

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

        // 1s, 2s, 4s ... 32s, then 60s for every later attempt.
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

        // Orleans timers cannot wait arbitrarily long, so a far-off deadline is armed for at most 30 days; when that
        // timer fires early, DeadlineFired sees the deadline is not due yet and arms it again.
        if (delay > s_maxTimerDelay)
        {
            delay = s_maxTimerDelay;
        }

        // KeepAlive = true: a pending deadline keeps the room activated so the timer can fire.
        _deadlineTimers[name] = this.RegisterGrainTimer(() => DeadlineFired(name),
            new GrainTimerCreationOptions { DueTime = delay, Period = Timeout.InfiniteTimeSpan, KeepAlive = true });
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

        // Cancelled (or the room closed) after this timer was armed.
        if (!Record.Exists || !Record.Deadlines.TryGetValue(name, out DateTimeOffset due))
        {
            return;
        }

        // Fired early: the 30-day cap, or timer imprecision beyond a small tolerance. Wait for the real due time.
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
                _log.LogError(failure, "OnDeadline('{Deadline}') failed in room {Room}; it will be tried again.", name,
                    RoomId);
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
    //
    // Clients may resend a command (for example after a reconnect) without knowing whether it already ran. Each
    // player's recent successful replies are kept in the record by command id, so a resend returns the first
    // outcome instead of running the handler twice. Being in the record, the log survives restarts with the state.

    private bool TryGetRecorded(string player, string commandId, [NotNullWhen(true)] out RoomReply? reply)
    {
        if (Record.Dedup.TryGetValue(player, out List<DedupEntry>? entries))
        {
            // Newest first: a retry is most likely of a recent command.
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

        // The snapshot is left out: it is large, and it would be out of date by the time a retry reads it.
        entries.Add(new DedupEntry { CommandId = commandId, At = Now, Reply = reply.WithoutSnapshot() });

        // Keep the newest DedupCapacity entries, and anything younger than DedupWindow, whichever is more.
        // Entries are in time order, so trimming walks from the oldest. hardExcess is a safety cap: past
        // 16 × DedupCapacity entries the oldest go even if they are still inside the window.
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
        new(new PlayerId(member.Player), member.Role,
            _runtime.TryGetValue(member.Player, out MemberRuntime? runtime)
                ? runtime.Status
                : PresenceStatus.Reconnecting);

    /// <summary>
    /// Builds what a connection needs to (re)start following the room: the viewer's own view of the state, the member
    /// list, and the stream position (incarnation, epoch, At) that the next events continue from.
    /// </summary>
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
                Status = _runtime.TryGetValue(member.Player, out MemberRuntime? runtime)
                    ? runtime.Status
                    : PresenceStatus.Reconnecting,
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

    /// <summary>
    /// Turns app-code failure into a reply. A deliberate rejection passes its message to the client; any other
    /// exception is a bug, so it is logged and the client gets a generic error without internal details.
    /// </summary>
    private RoomReply FailureReply(Exception failure, string rejectionCode, string what)
    {
        if (failure is RoomRejectedException rejected)
        {
            return RoomReply.Rejected(rejected.Code ?? rejectionCode, rejected.Message);
        }

        _log.LogError(failure, "{What} failed in room {RoomType}/{Room}.", what, RoomType, RoomId);
        return RoomReply.Error(Wire.Codes.HandlerError, "The room could not process this request.");
    }

    // A framework event about a member. By default it goes to everyone except that member, who already knows.
    private OutEvent PresenceEvent(string player, PresenceStatus status, string? role, bool includeSelf = false) =>
        new()
        {
            Type = Wire.Types.Presence,
            Data = FrameworkJson.Serialize(new PresenceNotice(player, StatusName(status), role)),
            Except = includeSelf ? null : player,
        };

    // Tells specific connections they are out of the room. Not sequenced: it ends the stream rather than continuing it.
    private OutEvent ClosedNotice(string reason, string? message, List<(string Gateway, string ClientId)> targets) =>
        new()
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

    /// <summary>
    /// One unit of room work, from <c>Begin</c> to <c>End</c>.
    /// </summary>
    private sealed class Operation
    {
        // The outbox app code currently writes to. Guarded swaps in a nested scope and merges it back on success.
        public Scope Current = new();

        // Presence changes made during the operation, for OnPresenceChanged to see at commit time.
        public readonly List<(string Player, PresenceStatus Status)> PresenceChanges = [];

        // Something durable changed, so CommitAsync must write the record.
        public bool NeedsSave;

        // The state was reloaded mid-operation (RollbackMode.None failure); the operation must not commit.
        public bool Poisoned;
    }

    /// <summary>
    /// What app code asked for during one guarded call; merged into the operation only if the call succeeds.
    /// </summary>
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

    /// <summary>
    /// An event waiting for commit. Addressed by <see cref="Targets"/> (explicit connections), else
    /// <see cref="Only"/> (one member), else every member but <see cref="Except"/>.
    /// </summary>
    private sealed class OutEvent
    {
        public required string Type;
        public byte[]? Data;
        public string? Only;
        public string? Except;
        public bool Sequenced = true;
        public List<(string Gateway, string ClientId)>? Targets;
    }

    // A member to remove. ClosedReason (kicked / removed) is sent to their connections; null sends nothing.
    private sealed record Removal(string Player, string? ClosedReason, string? Message);

    /// <summary>
    /// The non-durable side of a member in this activation.
    /// </summary>
    private sealed class MemberRuntime
    {
        // Sequence number of the last event addressed to this member in the current epoch.
        public long Seq;
        public PresenceStatus Status;

        // Running while the member is "reconnecting"; removes them when it fires.
        public IGrainTimer? GraceTimer;

        // Attached connections by MQTT client id. A member can have several (one per tab or device).
        public readonly Dictionary<string, AttachedConnection> Connections = new(StringComparer.Ordinal);
    }

    // Which generation of a client id is attached, and through which gateway its events go.
    private readonly record struct AttachedConnection(long Generation, string Gateway);

    // A gateway this room delivers to, and when it last called (see CheckLeases).
    private sealed class GatewayLease
    {
        public IGatewayObserver Observer = null!;
        public DateTimeOffset LastRenewal;
    }
}
