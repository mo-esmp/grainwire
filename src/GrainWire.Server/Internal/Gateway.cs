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
using Orleans.Runtime;

namespace GrainWire.Server.Internal;

/// <summary>
/// The gateway component: enforces the MQTT policy, routes client publishes to room grains, delivers room events to
/// inboxes, and keeps a lease with every room it holds connections in. It depends only on <see cref="IGrainFactory"/>,
/// so the same code runs inside a silo or in an Orleans client process.
/// </summary>
internal sealed partial class Gateway : IGatewayObserver
{
    private const string ConnectionKey = "grainwire.connection";

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
    private readonly object _refGate = new();
    private MqttServer? _server;
    private GatewayRef? _ref;

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

        if (!result.Succeeded || string.IsNullOrEmpty(result.Player.Value))
        {
            e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            e.ReasonString = result.FailureReason ?? "Authentication failed.";
            return;
        }

        if (_connectionsPerPlayer.TryGetValue(result.Player.Value, out var open) && open >= _options.MaxConnectionsPerPlayer)
        {
            e.ReasonCode = MqttConnectReasonCode.QuotaExceeded;
            e.ReasonString = "Too many connections for this player.";
            return;
        }

        e.SessionItems[ConnectionKey] = new GatewayConnection(e.ClientId, result.Player, result.Claims, _options);
        e.ResponseUserProperties = [new MqttUserProperty("gw-player", (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(result.Player.Value))];
    }

    private Task OnClientConnected(ClientConnectedEventArgs e)
    {
        if (e.SessionItems[ConnectionKey] is not GatewayConnection connection) return Task.CompletedTask;
        _connections[connection.ClientId] = connection;
        _connectionsPerPlayer.AddOrUpdate(connection.Player.Value, 1, static (_, count) => count + 1);
        connection.Worker = Task.Run(() => ProcessQueueAsync(connection));
        return Task.CompletedTask;
    }

    private Task OnClientDisconnected(ClientDisconnectedEventArgs e)
    {
        if (e.SessionItems[ConnectionKey] is not GatewayConnection connection || connection.Closed) return Task.CompletedTask;
        connection.Closed = true;
        _connections.TryRemove(new KeyValuePair<string, GatewayConnection>(connection.ClientId, connection));
        _connectionsPerPlayer.AddOrUpdate(connection.Player.Value, 0, static (_, count) => Math.Max(0, count - 1));
        if (_connectionsPerPlayer.TryGetValue(connection.Player.Value, out var left) && left == 0)
        {
            _connectionsPerPlayer.TryRemove(new KeyValuePair<string, int>(connection.Player.Value, 0));
        }

        connection.Queue.Writer.TryComplete();
        _ = Task.Run(() => CleanUpAsync(connection));
        return Task.CompletedTask;
    }

    private Task OnSubscription(InterceptingSubscriptionEventArgs e)
    {
        // A connection may subscribe to exactly one topic: its own inbox. No wildcards, no shared subscriptions.
        if (e.SessionItems[ConnectionKey] is GatewayConnection connection && e.TopicFilter.Topic == Wire.InboxTopic(connection.ClientId)) return Task.CompletedTask;
        e.ProcessSubscription = false;
        e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
        e.ReasonString = "A connection may only subscribe to its own inbox.";
        return Task.CompletedTask;
    }

    private Task OnPublish(InterceptingPublishEventArgs e)
    {
        // Messages the gateway injected itself. Client ids cannot start with '$', so nobody else can claim this id.
        if (e.ClientId == Wire.GatewaySender) return Task.CompletedTask;

        // Nothing a client publishes is ever routed to another client: it goes to a room grain or nowhere.
        e.ProcessPublish = false;
        var message = e.ApplicationMessage;
        if (e.SessionItems[ConnectionKey] is not GatewayConnection connection || connection.Closed) return Refuse(e, MqttPubAckReasonCode.NotAuthorized, "Not connected.");
        if (message.Retain) return Refuse(e, MqttPubAckReasonCode.NotAuthorized, "Retained messages are not allowed.");
        if (!ClientTopic.TryParse(message.Topic, out var topic)) return Refuse(e, MqttPubAckReasonCode.NotAuthorized, "Clients may only publish to rooms/{type}/{id}/cmd/{name} or rooms/{type}/{id}/sys/{name}.");
        if (message.Payload.Length > _options.MaxPayloadBytes) return Refuse(e, MqttPubAckReasonCode.QuotaExceeded, "The message is too large.");

        var payload = message.Payload.IsEmpty ? [] : System.Buffers.BuffersExtensions.ToArray(message.Payload);
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
        if (!_connections.TryGetValue(clientId, out var connection) || connection.Closed) return Task.CompletedTask;
        connection.Closed = true;
        _log.LogWarning("Disconnecting {ClientId}: its outgoing queue overflowed.", clientId);
        _ = Task.Run(async () =>
        {
            try
            {
                await _server!.DisconnectClientAsync(clientId, new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.QuotaExceeded, ReasonString = "Outgoing queue overflow." });
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not disconnect slow consumer {ClientId}.", clientId);
            }
            finally
            {
                connection.Closed = false;
            }
        });
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- routing client requests to rooms

    private async Task ProcessQueueAsync(GatewayConnection connection)
    {
        await foreach (var inbound in connection.Queue.Reader.ReadAllAsync())
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
        var topic = inbound.Topic;
        var room = topic.Room;
        var roomKey = room.ToString();
        if (!ClientMessage.TryParse(inbound.Payload, out var message))
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

        if (reply.Snapshot is not null) await PublishAsync(connection.ClientId, Envelopes.Snapshot(roomKey, reply.Snapshot));
        if (!message.NoReply) await PublishAsync(connection.ClientId, Envelopes.Reply(roomKey, message.CommandId, reply));
    }

    private async Task<RoomReply> JoinAsync(GatewayConnection connection, RoomAddress room, string roomKey, ClientMessage message)
    {
        if (!connection.Rooms.ContainsKey(roomKey) && connection.Rooms.Count >= _options.MaxRoomsPerConnection)
        {
            return RoomReply.Rejected(Wire.Codes.TooManyRooms, "This connection is in too many rooms.");
        }

        // A player who has used up their creation budget can still join rooms that exist.
        var mayCreate = message.Create && _creations.HasRoom(connection.Player.Value);
        var reply = await CallAsync(room, grain => grain.Join(new JoinRequest
        {
            Gateway = Ref,
            Connection = connection.Id,
            Player = connection.Player,
            Claims = connection.Claims.Count == 0 ? null : new Dictionary<string, string>(connection.Claims),
            Create = mayCreate,
            Options = message.Options,
            Data = message.JoinData,
        }));

        if (reply.Status == ReplyStatus.Ok)
        {
            if (reply.Code == Wire.Codes.Created) _creations.Record(connection.Player.Value);
            connection.Rooms[roomKey] = room;
            _directory.GetOrAdd(roomKey, static _ => new RoomAttachments()).Connections[connection.ClientId] = connection;
            if (connection.Closed) await DropRoomAsync(connection, roomKey, room, detach: true);
        }
        else if (message.Create && !mayCreate && reply.Code == Wire.Codes.RoomNotFound)
        {
            reply = RoomReply.Rejected(Wire.Codes.RateLimited, "You are creating rooms too quickly.");
        }

        return reply;
    }

    private async Task<RoomReply> LeaveAsync(GatewayConnection connection, RoomAddress room, string roomKey)
    {
        if (!connection.Rooms.ContainsKey(roomKey)) return RoomReply.Ok(Wire.Codes.Left);
        await DropRoomAsync(connection, roomKey, room, detach: false);
        return await CallAsync(room, grain => grain.Leave(new LeaveRequest { Gateway = Id, Connection = connection.Id, Player = connection.Player }));
    }

    private async Task<RoomReply> SyncAsync(GatewayConnection connection, RoomAddress room, string roomKey, ClientMessage message)
    {
        if (!connection.Rooms.ContainsKey(roomKey)) return RoomReply.Error(Wire.Codes.NotJoined, "Join the room first.");
        var reply = await CallAsync(room, grain => grain.Sync(new SyncRequest
        {
            Gateway = Ref,
            Connection = connection.Id,
            Player = connection.Player,
            Incarnation = message.Incarnation,
            Epoch = message.Epoch,
            Seq = message.Seq,
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
        if (reply.Status != ReplyStatus.Error || reply.Code is not (Wire.Codes.RoomNotFound or Wire.Codes.NotMember)) return;
        connection.Rooms.TryRemove(roomKey, out _);
        if (_directory.TryGetValue(roomKey, out var attachments)) attachments.Connections.TryRemove(new KeyValuePair<string, GatewayConnection>(connection.ClientId, connection));
    }

    private async Task ReplyWithoutRoomAsync(GatewayConnection connection, ClientTopic topic, byte[] payload, RoomReply reply)
    {
        try
        {
            if (ClientMessage.TryParse(payload, out var message) && message is { CommandId: not null, NoReply: false })
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
        if (_knownRoomTypes.ContainsKey(roomType)) return true;
        var grainType = RoomAddress.GrainTypeFor(roomType);
        foreach (var manifest in _manifest.Current.AllGrainManifests)
        {
            if (!manifest.Grains.ContainsKey(grainType)) continue;
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
            foreach (var item in batch.Items)
            {
                if (!item.Sequenced)
                {
                    var notice = Envelopes.Notice(batch, item);
                    foreach (var target in item.Targets)
                    {
                        if (!_connections.TryGetValue(target.ClientId, out var connection) || !connection.Rooms.TryGetValue(batch.Room, out var room)) continue;
                        await PublishAsync(target.ClientId, notice);
                        if (item.Type == Wire.Types.Closed) await DropRoomAsync(connection, batch.Room, room, detach: false);
                    }

                    continue;
                }

                var template = new EventTemplate(batch, item);
                foreach (var target in item.Targets)
                {
                    if (_connections.TryGetValue(target.ClientId, out var connection) && connection.Rooms.ContainsKey(batch.Room))
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
        if (OutboundFilter is { } filter && !filter(clientId, payload)) return Task.CompletedTask;
        var message = new MqttApplicationMessage
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
            if (_ref is not null) return _ref;
            lock (_refGate)
            {
                return _ref ??= new GatewayRef { Id = Id, Observer = _grains.CreateObjectReference<IGatewayObserver>(this) };
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
        for (var i = _pendingDetaches.Count; i > 0 && _pendingDetaches.TryDequeue(out var pending); i--)
        {
            await DetachAsync(pending.Room, pending.Connection);
        }

        _sequencer.Prune(TimeSpan.FromMinutes(10));
        var rooms = new List<KeyValuePair<string, RoomAttachments>>();
        foreach (var entry in _directory)
        {
            if (entry.Value.Connections.IsEmpty) _directory.TryRemove(entry);
            else rooms.Add(entry);
        }

        await Parallel.ForEachAsync(rooms, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken }, async (entry, _) => await RenewRoomAsync(entry.Key, entry.Value));
    }

    private async Task RenewRoomAsync(string roomKey, RoomAttachments attachments)
    {
        var listed = attachments.Connections.Values.ToList();
        if (listed.Count == 0) return;
        var room = listed[0].Rooms.TryGetValue(roomKey, out var address) ? address : ParseRoomKey(roomKey);
        RenewReply reply;
        try
        {
            reply = await _grains.GetGrain<IRoomGrain>(room.ToGrainId()).Renew(new RenewRequest
            {
                Gateway = Ref,
                Connections = listed.ConvertAll(c => new RenewEntry { Connection = c.Id, Player = c.Player }),
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
                foreach (var connection in listed) await CloseLocallyAsync(connection, roomKey, room, Wire.ClosedReasons.Lost);
                return;
            }

            foreach (var notMember in reply.NotMembers)
            {
                if (_connections.TryGetValue(notMember.ClientId, out var connection) && connection.Generation == notMember.Generation)
                {
                    await CloseLocallyAsync(connection, roomKey, room, Wire.ClosedReasons.Removed);
                }
            }

            foreach (var snapshot in reply.Snapshots)
            {
                if (_connections.TryGetValue(snapshot.Connection.ClientId, out var connection) && connection.Generation == snapshot.Connection.Generation)
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
        if (!connection.Rooms.ContainsKey(roomKey)) return;
        await DropRoomAsync(connection, roomKey, room, detach: false);
        await PublishAsync(connection.ClientId, Envelopes.Closed(roomKey, null, reason, null));
    }

    private async Task DropRoomAsync(GatewayConnection connection, string roomKey, RoomAddress room, bool detach)
    {
        connection.Rooms.TryRemove(roomKey, out _);
        if (_directory.TryGetValue(roomKey, out var attachments)) attachments.Connections.TryRemove(new KeyValuePair<string, GatewayConnection>(connection.ClientId, connection));
        if (detach) await DetachAsync(room, connection.Id);
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

        foreach (var (roomKey, room) in connection.Rooms.ToArray())
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
        var slash = roomKey.IndexOf('/');
        return new RoomAddress(roomKey[..slash], roomKey[(slash + 1)..]);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$")]
    private static partial Regex ClientIdRegex();

    private sealed class RoomAttachments
    {
        public ConcurrentDictionary<string, GatewayConnection> Connections { get; } = new(StringComparer.Ordinal);
    }
}
