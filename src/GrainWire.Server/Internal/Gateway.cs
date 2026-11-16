using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using GrainWire.Protocol;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Orleans.Metadata;

namespace GrainWire.Server.Internal;

/// <summary>
/// The gateway component: enforces the MQTT policy, routes client publishes to room grains, delivers room events to
/// inboxes, and keeps a lease with every room it holds connections in. It depends only on <see cref="IGrainFactory"/>,
/// so the same code runs inside a silo or in an Orleans client process.
/// </summary>
internal sealed partial class Gateway : IGatewayObserver
{
    private const string ConnectionKey = "grainwire.connection";
    private const string TooManyConnections = "Too many connections for this player.";

    private readonly IGrainFactory _grains;
    private readonly IClusterManifestProvider _manifest;
    private readonly IConnectionAuthenticator _authenticator;
    private readonly GrainWireGatewayOptions _options;
    private readonly ILogger<Gateway> _log;
    private readonly PerMinuteLimiter _creations;
    private readonly BatchSequencer _sequencer;

    private readonly ConcurrentDictionary<string, GatewayConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _connectionsPerPlayer = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RoomAttachments> _directory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _knownRoomTypes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(RoomAddress Room, ConnectionId Connection)> _pendingDetaches = new();
    private readonly Lock _refGate = new();
    private MqttServer? _server;

    public Gateway(IGrainFactory grains, IClusterManifestProvider manifest, IConnectionAuthenticator authenticator, GrainWireGatewayOptions options, ILogger<Gateway> log)
    {
        _grains = grains;
        _manifest = manifest;
        _authenticator = authenticator;
        _options = options;
        _log = log;
        _creations = new PerMinuteLimiter(options.RoomCreationsPerMinute);
        _sequencer = new BatchSequencer(PublishBatchAsync, TimeSpan.FromMilliseconds(250));
    }

    public GatewayId Id { get; } = new(Guid.NewGuid().ToString("N"));

    public int ConnectionCount => _connections.Count;

    public void Attach(MqttServer server)
    {
        _server = server;
        server.ValidatingConnectionAsync += OnValidatingConnection;
        server.ClientConnectedAsync += OnClientConnected;
        server.ClientDisconnectedAsync += OnClientDisconnected;
        server.InterceptingSubscriptionAsync += OnSubscription;
        server.InterceptingPublishAsync += OnPublish;
        server.ApplicationMessageEnqueuedOrDroppedAsync += e => e.IsDropped ? OnOutgoingQueueOverflow(e.ReceiverClientId) : Task.CompletedTask;
        server.QueuedApplicationMessageOverwrittenAsync += e => OnOutgoingQueueOverflow(e.ReceiverClientId);
    }

    // ---------------------------------------------------------------- MQTT policy

    private async Task OnValidatingConnection(ValidatingConnectionEventArgs e)
    {
        if (e.ProtocolVersion != MqttProtocolVersion.V500)
        {
            e.ReasonCode = MqttConnectReasonCode.UnsupportedProtocolVersion;
            return;
        }

        if (e.ClientId is null || !ClientIdRegex().IsMatch(e.ClientId))
        {
            e.ReasonCode = MqttConnectReasonCode.ClientIdentifierNotValid;
            e.ReasonString = "The client id must be 16 to 128 characters of A-Z, a-z, 0-9, '-' or '_'.";
            return;
        }

        AuthenticationResult result;
        try
        {
            result = await _authenticator.AuthenticateAsync(new ConnectionAuthContext
            {
                ClientId = e.ClientId,
                UserName = e.UserName,
                Token = e.Password,
                RemoteEndPoint = e.RemoteEndPoint,
                IsSecureConnection = e.IsSecureConnection,
            }, e.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The connection authenticator failed.");
            e.ReasonCode = MqttConnectReasonCode.ServerUnavailable;
            return;
        }

        if (!result.Succeeded || result.Player.Value is not { Length: > 0 and <= 256 })
        {
            e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            e.ReasonString = result.FailureReason ?? "Authentication failed.";
            return;
        }

        // The usual refusal. Connections that are validated at the same moment can all pass this check; the count in
        // OnClientConnected is what enforces the limit.
        if (_connectionsPerPlayer.TryGetValue(result.Player.Value, out int open) && open >= _options.MaxConnectionsPerPlayer)
        {
            e.ReasonCode = MqttConnectReasonCode.QuotaExceeded;
            e.ReasonString = TooManyConnections;
            return;
        }

        e.SessionItems[ConnectionKey] = new GatewayConnection(e.ClientId, result.Player, result.Claims, _options);
        e.ResponseUserProperties = [new MqttUserProperty("gw-player", (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(result.Player.Value))];
    }

    private Task OnClientConnected(ClientConnectedEventArgs e)
    {
        if (e.SessionItems[ConnectionKey] is not GatewayConnection connection)
        {
            return Task.CompletedTask;
        }

        // Count, then check. The increment is atomic, so of several connections that passed validation together only
        // as many as the limit allows are admitted. A slot is held only by a connection that really connected, and is
        // given back when that connection ends, so no slot can leak when a CONNECT is abandoned half-way.
        int open = _connectionsPerPlayer.AddOrUpdate(connection.Player.Value, 1, static (_, count) => count + 1);
        if (open > _options.MaxConnectionsPerPlayer)
        {
            ReleaseConnectionSlot(connection.Player.Value);
            connection.Closed = true;
            _log.LogDebug("Disconnecting {ClientId}: its player has too many connections.", connection.ClientId);
            _ = Task.Run(() => DisconnectAsync(connection.ClientId, TooManyConnections));
            return Task.CompletedTask;
        }

        connection.Admitted = true;
        _connections[connection.ClientId] = connection;
        connection.Worker = Task.Run(() => ProcessQueueAsync(connection));
        return Task.CompletedTask;
    }

    private void ReleaseConnectionSlot(string player)
    {
        _connectionsPerPlayer.AddOrUpdate(player, 0, static (_, count) => Math.Max(0, count - 1));
        _connectionsPerPlayer.TryRemove(new KeyValuePair<string, int>(player, 0));
    }

    private async Task DisconnectAsync(string clientId, string reason)
    {
        try
        {
            await _server!.DisconnectClientAsync(clientId, new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.QuotaExceeded, ReasonString = reason });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not disconnect {ClientId}.", clientId);
        }
    }

    private Task OnClientDisconnected(ClientDisconnectedEventArgs e)
    {
        if (e.SessionItems[ConnectionKey] is not GatewayConnection connection || connection.Closed)
        {
            return Task.CompletedTask;
        }

        connection.Closed = true;
        _connections.TryRemove(new KeyValuePair<string, GatewayConnection>(connection.ClientId, connection));

        // A connection that ended before it was counted (its CONNACK could not be sent) holds no slot.
        if (connection.Admitted)
        {
            connection.Admitted = false;
            ReleaseConnectionSlot(connection.Player.Value);
        }

        connection.Queue.Writer.TryComplete();
        _ = Task.Run(() => CleanUpAsync(connection));
        return Task.CompletedTask;
    }

    private Task OnSubscription(InterceptingSubscriptionEventArgs e)
    {
        // A connection may subscribe to exactly one topic: its own inbox. No wildcards, no shared subscriptions.
        if (e.SessionItems[ConnectionKey] is GatewayConnection connection && e.TopicFilter.Topic == Wire.InboxTopic(connection.ClientId))
        {
            return Task.CompletedTask;
        }

        e.ProcessSubscription = false;
        e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
        e.ReasonString = "A connection may only subscribe to its own inbox.";
        return Task.CompletedTask;
    }

    private Task OnPublish(InterceptingPublishEventArgs e)
    {
        // Messages the gateway injected itself. Client ids cannot start with '$', so nobody else can claim this id.
        if (e.ClientId == Wire.GatewaySender)
        {
            return Task.CompletedTask;
        }

        // Nothing a client publishes is ever routed to another client: it goes to a room grain or nowhere.
        e.ProcessPublish = false;
        MqttApplicationMessage message = e.ApplicationMessage;
        if (e.SessionItems[ConnectionKey] is not GatewayConnection connection || connection.Closed)
        {
            return Refuse(e, MqttPubAckReasonCode.NotAuthorized, "Not connected.");
        }

        if (message.Retain)
        {
            return Refuse(e, MqttPubAckReasonCode.NotAuthorized, "Retained messages are not allowed.");
        }

        if (!ClientTopic.TryParse(message.Topic, out ClientTopic topic))
        {
            return Refuse(e, MqttPubAckReasonCode.NotAuthorized, "Clients may only publish to rooms/{type}/{id}/cmd/{name} or rooms/{type}/{id}/sys/{name}.");
        }

        if (message.Payload.Length > _options.MaxPayloadBytes)
        {
            return Refuse(e, MqttPubAckReasonCode.QuotaExceeded, "The message is too large.");
        }

        byte[] payload = message.Payload.IsEmpty ? [] : System.Buffers.BuffersExtensions.ToArray(message.Payload);
        if (!connection.Bucket.TryTake())
        {
            _ = ReplyWithoutRoomAsync(connection, topic, payload, RoomReply.Rejected(Wire.Codes.RateLimited, "Too many commands; slow down."));
            return Refuse(e, MqttPubAckReasonCode.QuotaExceeded, Wire.Codes.RateLimited);
        }

        if (!connection.Queue.Writer.TryWrite(new InboundMessage(topic, payload)))
        {
            _ = ReplyWithoutRoomAsync(connection, topic, payload, RoomReply.Rejected(Wire.Codes.TooManyRequests, "Too many requests are waiting."));
            return Refuse(e, MqttPubAckReasonCode.QuotaExceeded, Wire.Codes.TooManyRequests);
        }

        return Task.CompletedTask;
    }

    private static Task Refuse(InterceptingPublishEventArgs e, MqttPubAckReasonCode code, string reason)
    {
        e.Response.ReasonCode = code;
        e.Response.ReasonString = reason;
        return Task.CompletedTask;
    }

    private Task OnOutgoingQueueOverflow(string clientId)
    {
        // The connection cannot keep up. Dropped events would only show up as gaps, so cut it loose: it reconnects and gets a snapshot.
        // Many messages can overflow at once; disconnect only once. The normal disconnect handling then cleans up.
        if (!_connections.TryGetValue(clientId, out GatewayConnection? connection) || Interlocked.Exchange(ref connection.Evicting, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _log.LogWarning("Disconnecting {ClientId}: its outgoing queue overflowed.", clientId);
        _ = Task.Run(() => DisconnectAsync(clientId, "Outgoing queue overflow."));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- routing client requests to rooms

    private async Task ProcessQueueAsync(GatewayConnection connection)
    {
        await foreach (InboundMessage inbound in connection.Queue.Reader.ReadAllAsync())
        {
            try
            {
                await HandleAsync(connection, inbound);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled error while processing a message from {ClientId}.", connection.ClientId);
            }
        }
    }

    private async Task HandleAsync(GatewayConnection connection, InboundMessage inbound)
    {
        ClientTopic topic = inbound.Topic;
        RoomAddress room = topic.Room;
        string roomKey = room.ToString();
        if (!ClientMessage.TryParse(inbound.Payload, out ClientMessage message))
        {
            _log.LogDebug("Dropping a message from {ClientId}: the payload is not a JSON object.", connection.ClientId);
            return;
        }

        RoomReply reply;
        if (!RoomTypeExists(room.Type))
        {
            reply = RoomReply.Error(Wire.Codes.UnknownRoomType, $"There is no room type '{room.Type}'.");
        }
        else if (topic.Kind == TopicKind.System)
        {
            reply = topic.Name switch
            {
                Wire.Sys.Join => await JoinAsync(connection, room, roomKey, message),
                Wire.Sys.Leave => await LeaveAsync(connection, room, roomKey),
                Wire.Sys.Sync => await SyncAsync(connection, room, roomKey, message),
                _ => RoomReply.Error(Wire.Codes.UnknownCommand, $"There is no system command '{topic.Name}'."),
            };
        }
        else if (!connection.Rooms.ContainsKey(roomKey))
        {
            reply = RoomReply.Error(Wire.Codes.NotJoined, "Join the room before sending commands.");
        }
        else
        {
            reply = await CallAsync(room, grain => grain.Command(new CommandRequest
            {
                Gateway = Ref,
                Connection = connection.Id,
                Player = connection.Player,
                Name = topic.Name,
                CommandId = message.CommandId,
                Incarnation = message.Incarnation,
                Data = message.Data,
            }));
            ForgetRoomIfGone(connection, roomKey, reply);
        }

        if (reply.Snapshot is not null)
        {
            await PublishAsync(connection.ClientId, Envelopes.Snapshot(roomKey, reply.Snapshot));
        }

        if (!message.NoReply)
        {
            await PublishAsync(connection.ClientId, Envelopes.Reply(roomKey, message.CommandId, reply));
        }
    }

    private async Task<RoomReply> JoinAsync(GatewayConnection connection, RoomAddress room, string roomKey, ClientMessage message)
    {
        if (!connection.Rooms.ContainsKey(roomKey) && connection.Rooms.Count >= _options.MaxRoomsPerConnection)
        {
            return RoomReply.Rejected(Wire.Codes.TooManyRooms, "This connection is in too many rooms.");
        }

        // A player who has used up their creation budget can still join rooms that exist. The slot is taken before
        // the room is asked, so requests that arrive together cannot all pass, and given back below if nothing was created.
        long creationTicket = 0;
        bool mayCreate = message.Create && _creations.TryReserve(connection.Player.Value, out creationTicket);
        RoomReply reply = await CallAsync(room, grain => grain.Join(new JoinRequest
        {
            Gateway = Ref,
            Connection = connection.Id,
            Player = connection.Player,
            Claims = connection.Claims.Count == 0 ? null : [with(connection.Claims)],
            Create = mayCreate,
            Options = message.Options,
            Data = message.JoinData,
            Known = _sequencer.Known(roomKey),
        }));

        // Keep the slot when the room was created, and also when the call failed without an answer: the room may
        // have been created all the same.
        if (mayCreate && reply.Code is not (Wire.Codes.Created or Wire.Codes.Unavailable))
        {
            _creations.Release(connection.Player.Value, creationTicket);
        }

        if (reply.Status == ReplyStatus.Ok)
        {
            connection.Rooms[roomKey] = room;
            _directory.GetOrAdd(roomKey, static _ => new RoomAttachments()).Connections[connection.ClientId] = connection;
            if (connection.Closed)
            {
                await DropRoomAsync(connection, roomKey, room, detach: true);
            }
        }
        else if (message.Create && !mayCreate && reply.Code == Wire.Codes.RoomNotFound)
        {
            reply = RoomReply.Rejected(Wire.Codes.RateLimited, "You are creating rooms too quickly.");
        }

        return reply;
    }

    private async Task<RoomReply> LeaveAsync(GatewayConnection connection, RoomAddress room, string roomKey)
    {
        if (!connection.Rooms.ContainsKey(roomKey))
        {
            return RoomReply.Ok(Wire.Codes.Left);
        }

        await DropRoomAsync(connection, roomKey, room, detach: false);
        return await CallAsync(room, grain => grain.Leave(new LeaveRequest { Gateway = Id, Connection = connection.Id, Player = connection.Player }));
    }

    private async Task<RoomReply> SyncAsync(GatewayConnection connection, RoomAddress room, string roomKey, ClientMessage message)
    {
        if (!connection.Rooms.ContainsKey(roomKey))
        {
            return RoomReply.Error(Wire.Codes.NotJoined, "Join the room first.");
        }

        RoomReply reply = await CallAsync(room, grain => grain.Sync(new SyncRequest
        {
            Gateway = Ref,
            Connection = connection.Id,
            Player = connection.Player,
            Incarnation = message.Incarnation,
            Epoch = message.Epoch,
            Seq = message.Seq,
            Known = _sequencer.Known(roomKey),
        }));
        ForgetRoomIfGone(connection, roomKey, reply);
        return reply;
    }

    private async Task<RoomReply> CallAsync(RoomAddress room, Func<IRoomGrain, Task<RoomReply>> call)
    {
        try
        {
            return await call(_grains.GetGrain<IRoomGrain>(room.ToGrainId()));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A call to room {Room} failed.", room);
            return RoomReply.Error(Wire.Codes.Unavailable, "The room is temporarily unavailable.");
        }
    }

    private void ForgetRoomIfGone(GatewayConnection connection, string roomKey, RoomReply reply)
    {
        if (reply.Status != ReplyStatus.Error || reply.Code is not (Wire.Codes.RoomNotFound or Wire.Codes.NotMember))
        {
            return;
        }

        connection.Rooms.TryRemove(roomKey, out _);
        if (_directory.TryGetValue(roomKey, out RoomAttachments? attachments))
        {
            attachments.Connections.TryRemove(new KeyValuePair<string, GatewayConnection>(connection.ClientId, connection));
        }
    }

    private async Task ReplyWithoutRoomAsync(GatewayConnection connection, ClientTopic topic, byte[] payload, RoomReply reply)
    {
        try
        {
            if (ClientMessage.TryParse(payload, out ClientMessage message) && message is { CommandId: not null, NoReply: false })
            {
                await PublishAsync(connection.ClientId, Envelopes.Reply(topic.Room.ToString(), message.CommandId, reply));
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not send a refusal to {ClientId}.", connection.ClientId);
        }
    }

    private bool RoomTypeExists(string roomType)
    {
        if (_knownRoomTypes.ContainsKey(roomType))
        {
            return true;
        }

        GrainType grainType = RoomAddress.GrainTypeFor(roomType);
        foreach (GrainManifest manifest in _manifest.Current.AllGrainManifests)
        {
            if (!manifest.Grains.ContainsKey(grainType))
            {
                continue;
            }

            _knownRoomTypes[roomType] = true;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- delivery from rooms

    /// <summary>Called by room grains (one-way). Batches are put back in order, then published to the inboxes they name.</summary>
    public Task Deliver(DeliveryBatch batch)
    {
        _sequencer.Accept(batch);
        return Task.CompletedTask;
    }

    private async Task PublishBatchAsync(DeliveryBatch batch)
    {
        try
        {
            foreach (DeliveryItem item in batch.Items)
            {
                if (!item.Sequenced)
                {
                    byte[] notice = Envelopes.Notice(batch, item);
                    foreach (DeliveryTarget target in item.Targets)
                    {
                        if (!_connections.TryGetValue(target.ClientId, out GatewayConnection? connection) || !connection.Rooms.TryGetValue(batch.Room, out RoomAddress room))
                        {
                            continue;
                        }

                        await PublishAsync(target.ClientId, notice);
                        if (item.Type == Wire.Types.Closed)
                        {
                            await DropRoomAsync(connection, batch.Room, room, detach: false);
                        }
                    }

                    continue;
                }

                EventTemplate template = new(batch, item);
                foreach (DeliveryTarget target in item.Targets)
                {
                    if (_connections.TryGetValue(target.ClientId, out GatewayConnection? connection) && connection.Rooms.ContainsKey(batch.Room))
                    {
                        await PublishAsync(target.ClientId, template.Render(target.Seq));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not deliver a batch from room {Room}.", batch.Room);
        }
    }

    /// <summary>Test hook: return false to drop a message before it is sent (simulates loss).</summary>
    internal Func<string, byte[], bool>? OutboundFilter { get; set; }

    private Task PublishAsync(string clientId, byte[] payload)
    {
        if (OutboundFilter is { } filter && !filter(clientId, payload))
        {
            return Task.CompletedTask;
        }

        MqttApplicationMessage message = new()
        {
            Topic = Wire.InboxTopic(clientId),
            PayloadSegment = new ArraySegment<byte>(payload),
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce,
        };
        return _server!.InjectApplicationMessage(new InjectedMqttApplicationMessage(message) { SenderClientId = Wire.GatewaySender }, CancellationToken.None);
    }

    // ---------------------------------------------------------------- leases and clean-up

    private GatewayRef Ref
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            lock (_refGate)
            {
                return field ??= new GatewayRef { Id = Id, Observer = _grains.CreateObjectReference<IGatewayObserver>(this) };
            }
        }
    }

    /// <summary>
    /// Renews this gateway's lease with every room it holds connections in. This refreshes the observer reference,
    /// proves the gateway is alive, and re-attaches connections to rooms that were re-activated on another silo.
    /// </summary>
    public async Task RenewLeasesAsync(CancellationToken cancellationToken)
    {
        // Disconnects a room could not be told about earlier.
        for (int i = _pendingDetaches.Count; i > 0 && _pendingDetaches.TryDequeue(out (RoomAddress Room, ConnectionId Connection) pending); i--)
        {
            await DetachAsync(pending.Room, pending.Connection);
        }

        _sequencer.Prune(TimeSpan.FromMinutes(10));
        List<KeyValuePair<string, RoomAttachments>> rooms = [];
        foreach (KeyValuePair<string, RoomAttachments> entry in _directory)
        {
            if (entry.Value.Connections.IsEmpty)
            {
                _directory.TryRemove(entry);
            }
            else
            {
                rooms.Add(entry);
            }
        }

        await Parallel.ForEachAsync(rooms, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken }, async (entry, _) => await RenewRoomAsync(entry.Key, entry.Value));
    }

    private async Task RenewRoomAsync(string roomKey, RoomAttachments attachments)
    {
        List<GatewayConnection> listed = [.. attachments.Connections.Values];
        if (listed.Count == 0)
        {
            return;
        }

        RoomAddress room = listed[0].Rooms.TryGetValue(roomKey, out RoomAddress address) ? address : ParseRoomKey(roomKey);
        RenewReply reply;
        try
        {
            reply = await _grains.GetGrain<IRoomGrain>(room.ToGrainId()).Renew(new RenewRequest
            {
                Gateway = Ref,
                Connections = listed.ConvertAll(c => new RenewEntry { Connection = c.Id, Player = c.Player }),
                Known = _sequencer.Known(roomKey),
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not renew the lease with room {Room}; trying again on the next round.", roomKey);
            return;
        }

        try
        {
            if (!reply.RoomExists)
            {
                foreach (GatewayConnection connection in listed)
                {
                    await CloseLocallyAsync(connection, roomKey, room, Wire.ClosedReasons.Lost);
                }

                return;
            }

            foreach (ConnectionId notMember in reply.NotMembers)
            {
                if (_connections.TryGetValue(notMember.ClientId, out GatewayConnection? connection) && connection.Generation == notMember.Generation)
                {
                    await CloseLocallyAsync(connection, roomKey, room, Wire.ClosedReasons.Removed);
                }
            }

            foreach (ConnectionSnapshot snapshot in reply.Snapshots)
            {
                if (_connections.TryGetValue(snapshot.Connection.ClientId, out GatewayConnection? connection) && connection.Generation == snapshot.Connection.Generation)
                {
                    await PublishAsync(connection.ClientId, Envelopes.Snapshot(roomKey, snapshot.Snapshot));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not apply the lease renewal of room {Room}.", roomKey);
        }
    }

    private async Task CloseLocallyAsync(GatewayConnection connection, string roomKey, RoomAddress room, string reason)
    {
        if (!connection.Rooms.ContainsKey(roomKey))
        {
            return;
        }

        await DropRoomAsync(connection, roomKey, room, detach: false);
        await PublishAsync(connection.ClientId, Envelopes.Closed(roomKey, null, reason, null));
    }

    private async Task DropRoomAsync(GatewayConnection connection, string roomKey, RoomAddress room, bool detach)
    {
        connection.Rooms.TryRemove(roomKey, out _);
        if (_directory.TryGetValue(roomKey, out RoomAttachments? attachments))
        {
            attachments.Connections.TryRemove(new KeyValuePair<string, GatewayConnection>(connection.ClientId, connection));
        }

        if (detach)
        {
            await DetachAsync(room, connection.Id);
        }
    }

    private async Task CleanUpAsync(GatewayConnection connection)
    {
        try
        {
            // Let requests that are already running finish, so a join cannot attach the connection after it was detached.
            await connection.Worker;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The worker of {ClientId} ended with an error.", connection.ClientId);
        }

        foreach ((string roomKey, RoomAddress room) in connection.Rooms.ToArray())
        {
            await DropRoomAsync(connection, roomKey, room, detach: true);
        }
    }

    private async Task DetachAsync(RoomAddress room, ConnectionId connection)
    {
        try
        {
            await _grains.GetGrain<IRoomGrain>(room.ToGrainId()).Detach(new DetachRequest { Gateway = Id, Connections = [connection] });
        }
        catch (Exception ex)
        {
            // Keep it and try again with the next lease round, otherwise the room would show this player as online forever.
            _log.LogDebug(ex, "Could not tell room {Room} about a disconnect; will retry.", room);
            _pendingDetaches.Enqueue((room, connection));
        }
    }

    private static RoomAddress ParseRoomKey(string roomKey)
    {
        int slash = roomKey.IndexOf('/');
        return new RoomAddress(roomKey[..slash], roomKey[(slash + 1)..]);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$")]
    private static partial Regex ClientIdRegex();

    private sealed class RoomAttachments
    {
        public ConcurrentDictionary<string, GatewayConnection> Connections { get; } = new(StringComparer.Ordinal);
    }
}
