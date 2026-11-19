using Orleans.Concurrency;

namespace GrainWire.Protocol;

// Contracts between the gateway and room grains. Application code does not use these directly.

/// <summary>
/// The grain interface every room implements. Called by gateways only.
/// </summary>
public interface IRoomGrain : IGrainWithStringKey
{
    /// <summary>
    /// Adds a connection to the room, creating the room when the request asks for it.
    /// </summary>
    /// <param name="request">The join request.</param>
    /// <returns>The outcome, with a snapshot when the join succeeded.</returns>
    Task<RoomReply> Join(JoinRequest request);

    /// <summary>
    /// Runs a command sent by a member.
    /// </summary>
    /// <param name="request">The command request.</param>
    /// <returns>The outcome, with the handler's return value as data.</returns>
    Task<RoomReply> Command(CommandRequest request);

    /// <summary>
    /// Brings a connection back in step with the room's event stream.
    /// </summary>
    /// <param name="request">The sync request, holding the position the client has reached.</param>
    /// <returns>The outcome, with a snapshot when the client had to be resynchronized.</returns>
    Task<RoomReply> Sync(SyncRequest request);

    /// <summary>
    /// Removes a connection's player from the room at the player's request.
    /// </summary>
    /// <param name="request">The leave request.</param>
    /// <returns>The outcome of the request.</returns>
    Task<RoomReply> Leave(LeaveRequest request);

    /// <summary>
    /// Tells the room that connections of a gateway are gone.
    /// </summary>
    /// <param name="request">The connections that were detached.</param>
    /// <returns>A task that completes when the room has processed the request.</returns>
    Task Detach(DetachRequest request);

    /// <summary>
    /// Renews a gateway's lease on its connections and attaches any the room does not yet know.
    /// </summary>
    /// <param name="request">The connections the gateway holds for this room.</param>
    /// <returns>Which connections are not members, plus snapshots for newly attached ones.</returns>
    Task<RenewReply> Renew(RenewRequest request);
}

/// <summary>
/// Implemented by each gateway process. Rooms push events through it with one-way calls.
/// </summary>
public interface IGatewayObserver : IGrainObserver
{
    /// <summary>
    /// Delivers events of one room to the connections this gateway holds.
    /// </summary>
    /// <param name="batch">The events and who receives them.</param>
    /// <returns>A task that completes when the batch is handed over.</returns>
    [OneWay]
    Task Deliver(DeliveryBatch batch);
}

/// <summary>
/// A gateway as a room sees it: its id and the observer the room delivers events through.
/// </summary>
[GenerateSerializer]
public sealed class GatewayRef
{
    /// <summary>
    /// The gateway's id.
    /// </summary>
    [Id(0)] public GatewayId Id { get; set; }

    /// <summary>
    /// The observer the room calls to deliver events.
    /// </summary>
    [Id(1)] public IGatewayObserver Observer { get; set; } = null!;
}

/// <summary>
/// The newest stream epoch of a room that a gateway has received batches from, with that epoch's order.
/// A gateway sends it whenever it attaches connections, so the room can rank its own epoch above it.
/// </summary>
/// <param name="Epoch">The epoch id, or null when the gateway has seen none.</param>
/// <param name="Order">The epoch's rank among the epochs of the room.</param>
[GenerateSerializer, Immutable]
public readonly record struct KnownEpoch([property: Id(0)] string? Epoch, [property: Id(1)] long Order);

/// <summary>
/// A request to join a room, sent by a gateway.
/// </summary>
[GenerateSerializer]
public sealed class JoinRequest
{
    /// <summary>
    /// The gateway holding the connection.
    /// </summary>
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;

    /// <summary>
    /// The connection that asks to join.
    /// </summary>
    [Id(1)] public ConnectionId Connection { get; set; }

    /// <summary>
    /// The authenticated player.
    /// </summary>
    [Id(2)] public PlayerId Player { get; set; }

    /// <summary>
    /// Claims of the authenticated connection.
    /// </summary>
    [Id(3)] public Dictionary<string, string>? Claims { get; set; }

    /// <summary>
    /// True when the join should create the room.
    /// </summary>
    [Id(4)] public bool Create { get; set; }

    /// <summary>
    /// Serialized room settings chosen by the creator.
    /// </summary>
    [Id(5)] public byte[]? Options { get; set; }

    /// <summary>
    /// Serialized app-defined join data.
    /// </summary>
    [Id(6)] public byte[]? Data { get; set; }

    /// <summary>
    /// The newest epoch the gateway has seen for this room.
    /// </summary>
    [Id(7)] public KnownEpoch Known { get; set; }
}

/// <summary>
/// A command sent by a member, forwarded by a gateway.
/// </summary>
[GenerateSerializer]
public sealed class CommandRequest
{
    /// <summary>
    /// The gateway holding the connection.
    /// </summary>
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;

    /// <summary>
    /// The connection the command came from.
    /// </summary>
    [Id(1)] public ConnectionId Connection { get; set; }

    /// <summary>
    /// The authenticated player.
    /// </summary>
    [Id(2)] public PlayerId Player { get; set; }

    /// <summary>
    /// The command name.
    /// </summary>
    [Id(3)] public string Name { get; set; } = "";

    /// <summary>
    /// Client-chosen id used to recognize a retry of the same command.
    /// </summary>
    [Id(4)] public string? CommandId { get; set; }

    /// <summary>
    /// The room incarnation the client believes it is talking to.
    /// </summary>
    [Id(5)] public string? Incarnation { get; set; }

    /// <summary>
    /// Serialized command payload.
    /// </summary>
    [Id(6)] public byte[]? Data { get; set; }
}

/// <summary>
/// A request to bring a connection back in step with the room's event stream.
/// </summary>
[GenerateSerializer]
public sealed class SyncRequest
{
    /// <summary>
    /// The gateway holding the connection.
    /// </summary>
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;

    /// <summary>
    /// The connection to synchronize.
    /// </summary>
    [Id(1)] public ConnectionId Connection { get; set; }

    /// <summary>
    /// The authenticated player.
    /// </summary>
    [Id(2)] public PlayerId Player { get; set; }

    /// <summary>
    /// The room incarnation the client believes it is talking to.
    /// </summary>
    [Id(3)] public string? Incarnation { get; set; }

    /// <summary>
    /// The stream epoch the client has reached.
    /// </summary>
    [Id(4)] public string? Epoch { get; set; }

    /// <summary>
    /// The last sequence number the client has received.
    /// </summary>
    [Id(5)] public long Seq { get; set; }

    /// <summary>
    /// The newest epoch the gateway has seen for this room.
    /// </summary>
    [Id(6)] public KnownEpoch Known { get; set; }
}

/// <summary>
/// A request to remove a player from a room at the player's own request.
/// </summary>
[GenerateSerializer]
public sealed class LeaveRequest
{
    /// <summary>
    /// The gateway holding the connection.
    /// </summary>
    [Id(0)] public GatewayId Gateway { get; set; }

    /// <summary>
    /// The connection the request came from.
    /// </summary>
    [Id(1)] public ConnectionId Connection { get; set; }

    /// <summary>
    /// The authenticated player.
    /// </summary>
    [Id(2)] public PlayerId Player { get; set; }
}

/// <summary>
/// Tells a room that connections held by a gateway are gone.
/// </summary>
[GenerateSerializer]
public sealed class DetachRequest
{
    /// <summary>
    /// The gateway that held the connections.
    /// </summary>
    [Id(0)] public GatewayId Gateway { get; set; }

    /// <summary>
    /// The connections that were detached.
    /// </summary>
    [Id(1)] public List<ConnectionId> Connections { get; set; } = [];
}

/// <summary>
/// A gateway's periodic renewal of its lease on the connections it holds for a room.
/// </summary>
[GenerateSerializer]
public sealed class RenewRequest
{
    /// <summary>
    /// The gateway that renews.
    /// </summary>
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;

    /// <summary>
    /// The connections the gateway holds for the room.
    /// </summary>
    [Id(1)] public List<RenewEntry> Connections { get; set; } = [];

    /// <summary>
    /// The newest epoch the gateway has seen for this room.
    /// </summary>
    [Id(2)] public KnownEpoch Known { get; set; }
}

/// <summary>
/// One connection named in a <see cref="RenewRequest"/>.
/// </summary>
[GenerateSerializer]
public sealed class RenewEntry
{
    /// <summary>
    /// The connection.
    /// </summary>
    [Id(0)] public ConnectionId Connection { get; set; }

    /// <summary>
    /// The player the connection belongs to.
    /// </summary>
    [Id(1)] public PlayerId Player { get; set; }
}

/// <summary>
/// The room's answer to a <see cref="RenewRequest"/>.
/// </summary>
[GenerateSerializer]
public sealed class RenewReply
{
    /// <summary>
    /// False when the room does not exist (any more): the gateway tells its connections the room was lost.
    /// </summary>
    [Id(0)] public bool RoomExists { get; set; }

    /// <summary>
    /// Connections whose player is not a member (any more).
    /// </summary>
    [Id(1)] public List<ConnectionId> NotMembers { get; set; } = [];

    /// <summary>
    /// Snapshots for connections that were attached by this renewal.
    /// </summary>
    [Id(2)] public List<ConnectionSnapshot> Snapshots { get; set; } = [];
}

/// <summary>
/// A snapshot addressed to one connection.
/// </summary>
[GenerateSerializer]
public sealed class ConnectionSnapshot
{
    /// <summary>
    /// The connection that receives the snapshot.
    /// </summary>
    [Id(0)] public ConnectionId Connection { get; set; }

    /// <summary>
    /// The snapshot.
    /// </summary>
    [Id(1)] public SnapshotData Snapshot { get; set; } = null!;
}

/// <summary>
/// Whether a request to a room succeeded.
/// </summary>
[GenerateSerializer]
public enum ReplyStatus
{
    /// <summary>
    /// The request succeeded.
    /// </summary>
    Ok = 0,

    /// <summary>
    /// The room refused the request and changed nothing.
    /// </summary>
    Rejected = 1,

    /// <summary>
    /// The request failed.
    /// </summary>
    Error = 2,
}

/// <summary>
/// Outcome of a request to a room.
/// </summary>
[GenerateSerializer]
public sealed class RoomReply
{
    /// <summary>
    /// Whether the request succeeded, was rejected or failed.
    /// </summary>
    [Id(0)] public ReplyStatus Status { get; set; }

    /// <summary>
    /// Machine-readable outcome code. See <see cref="Wire.Codes"/>.
    /// </summary>
    [Id(1)] public string? Code { get; set; }

    /// <summary>
    /// Human-readable explanation, for rejected and failed requests.
    /// </summary>
    [Id(2)] public string? Reason { get; set; }

    /// <summary>
    /// Serialized return value of the handler, if any.
    /// </summary>
    [Id(3)] public byte[]? Data { get; set; }

    /// <summary>
    /// A snapshot the gateway must deliver to the calling connection before the reply.
    /// </summary>
    [Id(4)] public SnapshotData? Snapshot { get; set; }

    /// <summary>
    /// The room incarnation, when the reply tells the client which one it is talking to.
    /// </summary>
    [Id(5)] public string? Incarnation { get; set; }

    /// <summary>
    /// Creates a successful reply.
    /// </summary>
    /// <param name="code">Optional outcome code.</param>
    /// <param name="data">Optional serialized return value.</param>
    /// <returns>The reply.</returns>
    public static RoomReply Ok(string? code = null, byte[]? data = null) => new() { Status = ReplyStatus.Ok, Code = code, Data = data };

    /// <summary>
    /// Creates a reply for a request the room refused.
    /// </summary>
    /// <param name="code">The outcome code.</param>
    /// <param name="reason">Optional explanation.</param>
    /// <returns>The reply.</returns>
    public static RoomReply Rejected(string code, string? reason = null) => new() { Status = ReplyStatus.Rejected, Code = code, Reason = reason };

    /// <summary>
    /// Creates a reply for a request that failed.
    /// </summary>
    /// <param name="code">The outcome code.</param>
    /// <param name="reason">Optional explanation.</param>
    /// <returns>The reply.</returns>
    public static RoomReply Error(string code, string? reason = null) => new() { Status = ReplyStatus.Error, Code = code, Reason = reason };

    /// <summary>
    /// Copies this reply without its snapshot.
    /// </summary>
    /// <returns>A new reply with every field but <see cref="Snapshot"/>.</returns>
    internal RoomReply WithoutSnapshot() => new() { Status = Status, Code = Code, Reason = Reason, Data = Data, Incarnation = Incarnation };
}

/// <summary>
/// A per-viewer baseline of room state, "as of" a position in that player's event stream.
/// </summary>
[GenerateSerializer]
public sealed class SnapshotData
{
    /// <summary>
    /// The room incarnation.
    /// </summary>
    [Id(0)] public string Incarnation { get; set; } = "";

    /// <summary>
    /// The stream epoch the snapshot belongs to.
    /// </summary>
    [Id(1)] public string Epoch { get; set; } = "";

    /// <summary>
    /// The sequence number the snapshot is current as of.
    /// </summary>
    [Id(2)] public long At { get; set; }

    /// <summary>
    /// Serialized view of the room state for this viewer.
    /// </summary>
    [Id(3)] public byte[]? View { get; set; }

    /// <summary>
    /// The room's members.
    /// </summary>
    [Id(4)] public List<MemberInfo> Members { get; set; } = [];

    /// <summary>
    /// The viewer's own membership.
    /// </summary>
    [Id(5)] public MemberInfo You { get; set; } = null!;
}

/// <summary>
/// A member as shown in a snapshot.
/// </summary>
[GenerateSerializer]
public sealed class MemberInfo
{
    /// <summary>
    /// The member's player id.
    /// </summary>
    [Id(0)] public string Player { get; set; } = "";

    /// <summary>
    /// The member's role, or null when it has none.
    /// </summary>
    [Id(1)] public string? Role { get; set; }

    /// <summary>
    /// Whether the member is online, reconnecting or gone.
    /// </summary>
    [Id(2)] public PresenceStatus Status { get; set; }
}

/// <summary>
/// Events from one room for the connections held by one gateway.
/// </summary>
[GenerateSerializer]
public sealed class DeliveryBatch
{
    /// <summary>
    /// The room's address, as <c>type/id</c>.
    /// </summary>
    [Id(0)] public string Room { get; set; } = "";

    /// <summary>
    /// The room incarnation.
    /// </summary>
    [Id(1)] public string Incarnation { get; set; } = "";

    /// <summary>
    /// The stream epoch the events belong to.
    /// </summary>
    [Id(2)] public string Epoch { get; set; } = "";

    /// <summary>
    /// The events, in order.
    /// </summary>
    [Id(3)] public List<DeliveryItem> Items { get; set; } = [];

    /// <summary>
    /// Counts up per room and gateway within an epoch, starting at 1, so a gateway can restore the order of batches.
    /// </summary>
    [Id(4)] public long Number { get; set; }

    /// <summary>
    /// Ranks the epochs of a room: a later epoch has a higher order. Epoch ids are random, so this is what lets a
    /// gateway tell a late batch of an older epoch from the first batch of a newer one.
    /// </summary>
    [Id(5)] public long EpochOrder { get; set; }
}

/// <summary>
/// One event in a <see cref="DeliveryBatch"/>, with the connections that receive it.
/// </summary>
[GenerateSerializer]
public sealed class DeliveryItem
{
    /// <summary>
    /// The envelope type (the <c>t</c> field): an app event name or one of <see cref="Wire.Types"/>.
    /// </summary>
    [Id(0)] public string Type { get; set; } = "";

    /// <summary>
    /// Serialized event payload.
    /// </summary>
    [Id(1)] public byte[]? Data { get; set; }

    /// <summary>
    /// False for notices outside the per-player sequence (<c>$closed</c>).
    /// </summary>
    [Id(2)] public bool Sequenced { get; set; } = true;

    /// <summary>
    /// The clients that receive the event, each with its own sequence number.
    /// </summary>
    [Id(3)] public List<DeliveryTarget> Targets { get; set; } = [];
}

/// <summary>
/// A client that receives an event, with the event's position in that client's stream.
/// </summary>
[GenerateSerializer]
public struct DeliveryTarget
{
    /// <summary>
    /// The MQTT client id of the receiver.
    /// </summary>
    [Id(0)] public string ClientId { get; set; }

    /// <summary>
    /// The event's sequence number in that client's stream.
    /// </summary>
    [Id(1)] public long Seq { get; set; }
}
