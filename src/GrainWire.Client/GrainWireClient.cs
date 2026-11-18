using System.Buffers;
using System.Text;
using System.Text.Json;
using GrainWire.Client.Internal;
using GrainWire.Client.Protocol;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace GrainWire.Client;

/// <summary>
/// A connection to a GrainWire gateway: MQTT 5 over WebSockets, subscribed to this client's inbox only. It reconnects
/// by itself with back-off, joins its rooms again afterwards, and re-sends requests that have no reply yet with their
/// original command ids.
/// </summary>
/// <remarks>
/// All members are thread-safe. Handlers and <see cref="StateChanged"/> are called one at a time, in order, and never
/// while the client holds a lock.
/// </remarks>
public sealed class GrainWireClient : IAsyncDisposable
{
    private static readonly MqttClientFactory s_factory = new();
    private static readonly TimeSpan s_tickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_housekeepingTimeout = TimeSpan.FromSeconds(5);

    // Guarded by Gate. Methods named ...Locked expect the caller to hold it. Nothing is awaited and no app code is
    // called while it is held: handlers go through Callbacks, which runs them after the lock is released.
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JoinOperation> _joining = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private IMqttClient? _mqtt;
    private Task _ticker = Task.CompletedTask;
    private Task _reconnector = Task.CompletedTask;
    private Action<ConnectionState>? _stateChanged;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _playerId;
    private TimeSpan _reconnectDelay;
    private bool _reconnecting;
    private bool _disposed;

    private GrainWireClient(GrainWireClientOptions options)
    {
        Options = options.Validated();
        ClientId = Options.ClientId!;
        Callbacks = new CallbackQueue(Options.MaxPendingHandlerCalls, Options.OnHandlerError);
        _reconnectDelay = Options.ReconnectDelay;
    }

    /// <summary>Called whenever <see cref="State"/> changes.</summary>
    public event Action<ConnectionState>? StateChanged
    {
        add
        {
            lock (Gate)
            {
                _stateChanged += value;
            }
        }

        remove
        {
            lock (Gate)
            {
                _stateChanged -= value;
            }
        }
    }

    /// <summary>The MQTT client id of this client. It names the inbox and stays the same across reconnects.</summary>
    public string ClientId { get; }

    public ConnectionState State
    {
        get
        {
            lock (Gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The player id the server derived from the credentials.</summary>
    public string? PlayerId
    {
        get
        {
            lock (Gate)
            {
                return _playerId;
            }
        }
    }

    /// <summary>The client's clock: milliseconds that never go backwards.</summary>
    internal static long Now => Environment.TickCount64;

    internal Lock Gate { get; } = new();

    internal GrainWireClientOptions Options { get; }

    internal CallbackQueue Callbacks { get; }

    internal bool IsConnectedLocked => _state == ConnectionState.Connected;

    /// <summary>Connects to a GrainWire gateway. Completes once the connection is up and the inbox is subscribed.</summary>
    /// <exception cref="ConnectException">The server refused the connection, could not be reached, or did not answer in time.</exception>
    public static async Task<GrainWireClient> ConnectAsync(GrainWireClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        GrainWireClient client = new(options);
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    /// <summary>
    /// Joins a room (creating it when <see cref="JoinOptions.Create"/> is set). Completes once the first snapshot has
    /// been processed, so the returned room has its view, members and <see cref="Room.You"/>. Joining a room this client
    /// is already in returns the same room.
    /// </summary>
    /// <param name="type">The room type, as in <c>[RoomType("chess")]</c> on the server.</param>
    /// <param name="id">The room id.</param>
    /// <param name="options">How to join. By default the room must exist.</param>
    /// <param name="cancellationToken">Stops waiting. It does not undo a join the server has already accepted.</param>
    /// <exception cref="RejectedException">The room refused the join.</exception>
    /// <exception cref="RoomException">The join could not be processed, for example because the room does not exist.</exception>
    /// <exception cref="OutcomeUnknownException">
    /// No reply arrived in time, or the server accepted the join but no snapshot arrived. The player may then be a
    /// member of the room; joining again is safe.
    /// </exception>
    /// <exception cref="NotConnectedException">There is no connection right now.</exception>
    public async Task<Room<TView>> JoinAsync<TView>(string type, string id, JoinOptions? options = null, CancellationToken cancellationToken = default)
    {
        Wire.EnsureName(type, nameof(type));
        Wire.EnsureName(id, nameof(id));
        cancellationToken.ThrowIfCancellationRequested();
        string key = $"{type}/{id}";
        byte[]? optionsJson = Payloads.Serialize(options?.Options, Options.SerializerOptions!);
        byte[]? dataJson = Payloads.Serialize(options?.Data, Options.SerializerOptions!);
        Task<Room> joining;
        lock (Gate)
        {
            // A second call while the first is still joining waits for the same outcome, so it never gets a half-joined room.
            if (_joining.TryGetValue(key, out JoinOperation? inFlight))
            {
                joining = inFlight.Completion.Task;
            }
            else if (_rooms.TryGetValue(key, out Room? existing))
            {
                return As<TView>(existing);
            }
            else if (_state != ConnectionState.Connected)
            {
                throw new NotConnectedException();
            }
            else
            {
                joining = StartJoinLocked(new Room<TView>(this, type, id, dataJson), options?.Create == true, optionsJson, dataJson);
            }
        }

        return As<TView>(await joining.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Joins a room and leaves its view as raw JSON. See <see cref="JoinAsync{TView}"/>.</summary>
    public Task<Room<JsonElement>> JoinAsync(string type, string id, JoinOptions? options = null, CancellationToken cancellationToken = default) =>
        JoinAsync<JsonElement>(type, id, options, cancellationToken);

    /// <summary>Closes the connection. Rooms stop receiving events; requests in flight fail with <see cref="NotConnectedException"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        IMqttClient? mqtt;
        Task ticker;
        Task reconnector;
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SetStateLocked(ConnectionState.Disconnected);
            NotConnectedException error = new();
            foreach (PendingRequest pending in _pending.Values.ToArray())
            {
                SettleLocked(pending, null, error);
            }

            foreach (Room room in _rooms.Values.ToArray())
            {
                room.FailWaitersLocked(error);
            }

            mqtt = _mqtt;
            ticker = _ticker;
            reconnector = _reconnector;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(ticker, reconnector).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (mqtt is not null)
        {
            try
            {
                if (mqtt.IsConnected)
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                    await mqtt.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The connection is already gone.
            }

            mqtt.Dispose();
        }

        _shutdown.Dispose();
    }

    // ---------------------------------------------------------------- used by Room

    internal static string NewCommandId() => Guid.NewGuid().ToString("N");

    /// <summary>The error a reply stands for, or null when it reports success.</summary>
    internal static Exception? ToException(ReplyEnvelope reply) => reply.Status switch
    {
        ReplyStatus.Ok => null,
        ReplyStatus.Rejected => new RejectedException(reply.Code ?? "rejected", reply.Reason),
        _ => new RoomException(reply.Code ?? "error", reply.Reason),
    };

    internal void ForgetLocked(Room room)
    {
        if (_rooms.TryGetValue(room.Key, out Room? known) && ReferenceEquals(known, room))
        {
            _rooms.Remove(room.Key);
        }

        // Commands in flight keep waiting for their own reply: the notice that closed the room can overtake the reply
        // to the very command that closed it. Only the room's housekeeping requests are abandoned.
        foreach (PendingRequest pending in _pending.Values.ToArray())
        {
            if (pending.RoomKey == room.Key && pending.Kind is RequestKind.Sync or RequestKind.Join)
            {
                SettleLocked(pending, null, new RoomClosedException(room.ClosedReasonLocked ?? RoomClosedReason.Left));
            }
        }
    }

    /// <summary>
    /// Sends a request. <paramref name="settled"/> is called once, under the lock, with the reply (whatever its status)
    /// or with the reason there is none. Returns null, without calling it, when the request cannot be sent because
    /// there is no connection. A command names the room it is sent through in <paramref name="room"/>.
    /// </summary>
    internal PendingRequest? RequestLocked(RequestKind kind, string roomKey, string path, Func<string, byte[]> payload, TimeSpan? timeout, Action<ReplyEnvelope?, Exception?> settled, Room? room = null)
    {
        // A join may be registered while the connection is down: it is sent once the connection is back.
        if (_disposed || (_state != ConnectionState.Connected && kind != RequestKind.Join))
        {
            return null;
        }

        string commandId = NewCommandId();
        long now = Now;
        long deadline = now + (long)(timeout ?? Options.RequestTimeout).TotalMilliseconds;
        PendingRequest pending = new(commandId, kind, roomKey, Wire.RoomTopic(roomKey, path), payload(commandId), deadline, settled) { Room = room };
        _pending.Add(commandId, pending);
        PublishLocked(pending);
        return pending;
    }

    /// <summary>Fire-and-forget.</summary>
    internal void PublishLocked(string topic, byte[] payload)
    {
        if (_mqtt is not { } mqtt)
        {
            return;
        }

        MqttApplicationMessage message = new()
        {
            Topic = topic,
            PayloadSegment = new ArraySegment<byte>(payload),
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce,
        };

        // Started under the lock so that messages reach the connection in the order they were sent here.
        _ = PublishAsync(mqtt, message);
    }

    /// <summary>Gives up a request the caller no longer waits for.</summary>
    internal void Abandon(PendingRequest pending, Exception reason)
    {
        lock (Gate)
        {
            SettleLocked(pending, null, reason);
        }
    }

    // ---------------------------------------------------------------- joining

    private static Room<TView> As<TView>(Room room) =>
        room as Room<TView> ?? throw new InvalidOperationException($"This client is already in room '{room.Key}' with another view type ({room.GetType().GenericTypeArguments[0].Name}).");

    private Task<Room> StartJoinLocked(Room room, bool create, byte[]? optionsJson, byte[]? dataJson)
    {
        // The room is known before the request is sent: the reply and the snapshot can arrive in either order.
        JoinOperation operation = new(room);
        _rooms[room.Key] = room;
        _joining[room.Key] = operation;
        RequestLocked(RequestKind.Join, room.Key, Wire.Join, commandId => ClientPayload.Join(commandId, create, optionsJson, dataJson), null, (reply, error) =>
        {
            Exception? failure = reply is null ? error : ToException(reply);
            if (failure is not null)
            {
                FailJoinLocked(operation, failure);
                return;
            }

            // The snapshot can be lost. If it is, the room asks for one with a sync (see OrderingEngine.ExpectBaseline);
            // this waits for whichever arrives.
            long now = Now;
            room.JoinedLocked(now);
            room.WhenReadyLocked(now + (long)Options.RequestTimeout.TotalMilliseconds, reply!.CommandId ?? string.Empty, notReady =>
            {
                if (notReady is null)
                {
                    _joining.Remove(room.Key);
                    operation.Completion.TrySetResult(room);
                }
                else
                {
                    FailJoinLocked(operation, notReady);
                }
            });
        });

        return operation.Completion.Task;
    }

    private void FailJoinLocked(JoinOperation operation, Exception error)
    {
        Room room = operation.Room;
        _joining.Remove(room.Key);
        bool outcomeUnknown = error is OutcomeUnknownException && room.ClosedReasonLocked is null;
        room.CloseLocked(RoomClosedReason.Removed, null, notify: false);

        // The server may have this connection in the room all the same: the reply was lost, or no snapshot followed
        // it. Stop the room's events from coming here. A gateway that never saw the join answers "left" as well.
        if (outcomeUnknown)
        {
            RequestLocked(RequestKind.Leave, room.Key, Wire.Leave, ClientPayload.Leave, s_housekeepingTimeout, static (_, _) => { });
        }

        operation.Completion.TrySetException(error);
    }

    // ---------------------------------------------------------------- connection

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        attempt.CancelAfter(Options.ConnectTimeout);
        try
        {
            await OpenConnectionAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new ConnectException("Timed out while connecting.", null);
        }
        catch (Exception exception) when (exception is not GrainWireClientException)
        {
            throw new ConnectException(exception.Message, null, exception);
        }

        lock (Gate)
        {
            _ticker = Task.Run(TickLoopAsync);
        }
    }

    /// <summary>One connection attempt: connect, subscribe to the inbox, then become the client's connection.</summary>
    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        string? token = Options.AccessTokenProvider is { } provider
            ? await provider(cancellationToken).ConfigureAwait(false)
            : Options.AccessToken;

        // Every attempt gets a new MQTT client, so nothing a broken connection leaves behind can affect the next one.
        IMqttClient mqtt = s_factory.CreateMqttClient();
        try
        {
            mqtt.ApplicationMessageReceivedAsync += e =>
            {
                Receive(e.ApplicationMessage.Payload);
                return Task.CompletedTask;
            };
            mqtt.DisconnectedAsync += _ =>
            {
                ConnectionLost(mqtt);
                return Task.CompletedTask;
            };

            MqttClientConnectResult connected = await mqtt.ConnectAsync(MqttOptions(token), cancellationToken).ConfigureAwait(false);
            if (connected.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new ConnectException(connected.ReasonString ?? $"The server refused the connection ({connected.ResultCode}).", (int)connected.ResultCode);
            }

            string? player = null;
            foreach (MqttUserProperty property in connected.UserProperties ?? [])
            {
                if (property.Name == Wire.PlayerProperty)
                {
                    player = Encoding.UTF8.GetString(property.ValueBuffer.Span);
                    break;
                }
            }

            MqttClientSubscribeResult subscribed = await mqtt.SubscribeAsync(Wire.InboxTopic(ClientId), MqttQualityOfServiceLevel.AtMostOnce, cancellationToken).ConfigureAwait(false);
            if (subscribed.Items.Count != 1 || subscribed.Items.First().ResultCode > MqttClientSubscribeResultCode.GrantedQoS2)
            {
                throw new ConnectException("The server refused the inbox subscription.", null);
            }

            IMqttClient? previous;
            lock (Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || !mqtt.IsConnected)
                {
                    throw new ConnectException("The connection was lost while it was being set up.", null);
                }

                previous = _mqtt;
                _mqtt = mqtt;
                _playerId = player;
                _reconnectDelay = Options.ReconnectDelay;
                _reconnecting = false;
                SetStateLocked(ConnectionState.Connected);
                ResumeLocked();
            }

            previous?.Dispose();
        }
        catch
        {
            mqtt.Dispose();
            throw;
        }
    }

    private MqttClientOptions MqttOptions(string? token)
    {
        // Clean start and no session expiry: the broker keeps no session state for GrainWire clients.
        MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
            .WithWebSocketServer(webSocket => webSocket.WithUri(Options.Url.AbsoluteUri))
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(ClientId)
            .WithCredentials(Options.Username, token)
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0)
            .WithTimeout(Options.ConnectTimeout);
        Options.ConfigureMqtt?.Invoke(builder);
        return builder.Build();
    }

    private void ConnectionLost(IMqttClient mqtt)
    {
        lock (Gate)
        {
            // An attempt that never became the client's connection reports its failure to whoever made it.
            if (_disposed || !ReferenceEquals(mqtt, _mqtt) || _state != ConnectionState.Connected)
            {
                return;
            }

            SetStateLocked(ConnectionState.Reconnecting);
            StartReconnectLocked();
        }
    }

    private void StartReconnectLocked()
    {
        if (_reconnecting || _disposed)
        {
            return;
        }

        _reconnecting = true;
        _reconnector = Task.Run(ReconnectLoopAsync);
    }

    private async Task ReconnectLoopAsync()
    {
        CancellationToken shutdown = _shutdown.Token;
        while (true)
        {
            TimeSpan delay;
            lock (Gate)
            {
                if (_disposed)
                {
                    return;
                }

                // Back off, with some spread so that many clients do not return in the same instant.
                delay = _reconnectDelay * (0.75 + (Random.Shared.NextDouble() * 0.5));
                _reconnectDelay = _reconnectDelay * 2 < Options.MaxReconnectDelay ? _reconnectDelay * 2 : Options.MaxReconnectDelay;
            }

            try
            {
                await Task.Delay(delay, shutdown).ConfigureAwait(false);
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                attempt.CancelAfter(Options.ConnectTimeout);
                await OpenConnectionAsync(attempt.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Refused, unreachable or too slow: try again after a longer delay.
            }
        }
    }

    /// <summary>
    /// After a (re)connect: join every room again, then re-send what has no reply yet, with the original command ids. A
    /// room that could not be joined yet keeps its commands until it is back.
    /// </summary>
    private void ResumeLocked()
    {
        Room[] rooms = [.. _rooms.Values.Where(room => room.NeedsRejoinLocked)];
        int waiting = rooms.Length;
        if (waiting == 0)
        {
            ResendCommandsLocked();
            return;
        }

        foreach (Room room in rooms)
        {
            room.RejoinLocked(() =>
            {
                if (--waiting == 0)
                {
                    ResendCommandsLocked();
                }
            });
        }
    }

    /// <summary>
    /// Sends the commands that still wait for a reply again, under their original command ids: all of them, or only
    /// those of one room. Commands of a room that is not joined at the moment stay back (see WaitsForRoomLocked).
    /// </summary>
    internal void ResendCommandsLocked(string? roomKey = null)
    {
        if (_state != ConnectionState.Connected)
        {
            return;
        }

        foreach (PendingRequest pending in _pending.Values.ToArray())
        {
            if (pending.Kind == RequestKind.Command && (roomKey is null || pending.RoomKey == roomKey) && !WaitsForRoomLocked(pending))
            {
                PublishLocked(pending);
            }
        }
    }

    /// <summary>
    /// True for a command whose room is not joined on the server just now, as far as the client knows. Sending it would
    /// only bring back "not-joined"; the room sends it once it is back. A command of a room that has closed has no room
    /// to wait for and is sent, so that its own reply can still arrive: also when the same room has been joined anew
    /// since, because that is another room object, which the command was not sent through.
    /// </summary>
    private bool WaitsForRoomLocked(PendingRequest pending) =>
        pending.Room is { IsAttachedLocked: false } room && _rooms.TryGetValue(room.Key, out Room? known) && ReferenceEquals(known, room);

    private void SetStateLocked(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        if (state != ConnectionState.Connected)
        {
            foreach (Room room in _rooms.Values)
            {
                room.DisconnectedLocked();
            }
        }

        if (_stateChanged is { } handlers)
        {
            foreach (Action<ConnectionState> handler in handlers.GetInvocationList().Cast<Action<ConnectionState>>())
            {
                Callbacks.Enqueue(() => handler(state));
            }
        }
    }

    // ---------------------------------------------------------------- messages

    private void PublishLocked(PendingRequest pending)
    {
        pending.LastSentAt = Now;

        // While there is no connection it stays pending, and is sent after the reconnect.
        if (_state == ConnectionState.Connected)
        {
            PublishLocked(pending.Topic, pending.Payload);
        }
    }

    private async Task PublishAsync(IMqttClient mqtt, MqttApplicationMessage message)
    {
        try
        {
            await mqtt.PublishAsync(message, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not sent, or not acknowledged. A request stays pending and is sent again by the retry timer or after the
            // reconnect; if it never gets a reply, its caller is told the outcome is unknown.
        }
    }

    private void Receive(ReadOnlySequence<byte> payload)
    {
        ReadOnlySpan<byte> bytes = payload.IsSingleSegment ? payload.FirstSpan : payload.ToArray();
        if (!InboxEnvelope.TryParse(bytes, out InboxEnvelope? envelope))
        {
            return;
        }

        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            if (envelope is ReplyEnvelope reply)
            {
                ReceiveReplyLocked(reply);
            }
            else if (_rooms.TryGetValue(envelope.Room, out Room? room))
            {
                room.ReceiveLocked(envelope, Now);
            }
        }
    }

    private void ReceiveReplyLocked(ReplyEnvelope reply)
    {
        if (reply.CommandId is null || !_pending.TryGetValue(reply.CommandId, out PendingRequest? pending))
        {
            return;
        }

        if (reply.Status == ReplyStatus.Error && pending.Kind == RequestKind.Command)
        {
            // The room could not be reached: the outcome is unknown, so keep the request and let the retry timer re-send it.
            if (reply.Code == Wire.Unavailable)
            {
                return;
            }

            // This gateway does not know the connection is in the room (it reconnected elsewhere): join again, then re-send.
            if (reply.Code == Wire.NotJoined && _rooms.TryGetValue(pending.RoomKey, out Room? room))
            {
                // If the join fails for now, the room tries again later and re-sends its commands once it is back.
                room.NotJoinedLocked(() =>
                {
                    if (room.IsAttachedLocked && _pending.ContainsKey(pending.CommandId))
                    {
                        PublishLocked(pending);
                    }
                });
                return;
            }
        }

        SettleLocked(pending, reply, null);
    }

    private void SettleLocked(PendingRequest pending, ReplyEnvelope? reply, Exception? error)
    {
        if (!_pending.Remove(pending.CommandId))
        {
            return;
        }

        pending.Settled(reply, reply is null ? error ?? new OutcomeUnknownException(pending.CommandId) : null);
    }

    private async Task TickLoopAsync()
    {
        using PeriodicTimer timer = new(s_tickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                lock (Gate)
                {
                    TickLocked(Now);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client was disposed.
        }
    }

    private void TickLocked(long now)
    {
        if (_disposed)
        {
            return;
        }

        // The connection can end without the MQTT client saying so; do not wait for the next publish to find out.
        if (_state == ConnectionState.Connected && _mqtt is { IsConnected: false })
        {
            SetStateLocked(ConnectionState.Reconnecting);
            StartReconnectLocked();
        }

        long retryAfter = (long)Options.RetryInterval.TotalMilliseconds;
        foreach (PendingRequest pending in _pending.Values.ToArray())
        {
            if (!_pending.ContainsKey(pending.CommandId))
            {
                continue;
            }

            if (now >= pending.Deadline)
            {
                SettleLocked(pending, null, new OutcomeUnknownException(pending.CommandId));
            }
            else if (_state == ConnectionState.Connected && now - pending.LastSentAt >= retryAfter && !WaitsForRoomLocked(pending))
            {
                PublishLocked(pending);
            }
        }

        Room[] rooms = [.. _rooms.Values];
        foreach (Room room in rooms)
        {
            room.ExpireWaitersLocked(now);
        }

        if (_state != ConnectionState.Connected)
        {
            return;
        }

        foreach (Room room in rooms)
        {
            room.TickLocked(now);
        }
    }
}
