using Orleans.Concurrency;

namespace GrainWire.Protocol;

// Contracts between the gateway and room grains. Application code does not use these directly.

/// <summary>The grain interface every room implements. Called by gateways only.</summary>
public interface IRoomGrain : IGrainWithStringKey
{
    Task<RoomReply> Join(JoinRequest request);
    Task<RoomReply> Command(CommandRequest request);
    Task<RoomReply> Sync(SyncRequest request);
    Task<RoomReply> Leave(LeaveRequest request);
    Task Detach(DetachRequest request);
    Task<RenewReply> Renew(RenewRequest request);
}

/// <summary>Implemented by each gateway process. Rooms push events through it with one-way calls.</summary>
public interface IGatewayObserver : IGrainObserver
{
    [OneWay]
    Task Deliver(DeliveryBatch batch);
}

[GenerateSerializer]
public sealed class GatewayRef
{
    [Id(0)] public GatewayId Id { get; set; }
    [Id(1)] public IGatewayObserver Observer { get; set; } = null!;
}

[GenerateSerializer]
public sealed class JoinRequest
{
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;
    [Id(1)] public ConnectionId Connection { get; set; }
    [Id(2)] public PlayerId Player { get; set; }
    [Id(3)] public Dictionary<string, string>? Claims { get; set; }
    [Id(4)] public bool Create { get; set; }
    [Id(5)] public byte[]? Options { get; set; }
    [Id(6)] public byte[]? Data { get; set; }
}

[GenerateSerializer]
public sealed class CommandRequest
{
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;
    [Id(1)] public ConnectionId Connection { get; set; }
    [Id(2)] public PlayerId Player { get; set; }
    [Id(3)] public string Name { get; set; } = "";
    [Id(4)] public string? CommandId { get; set; }
    [Id(5)] public string? Incarnation { get; set; }
    [Id(6)] public byte[]? Data { get; set; }
}

[GenerateSerializer]
public sealed class SyncRequest
{
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;
    [Id(1)] public ConnectionId Connection { get; set; }
    [Id(2)] public PlayerId Player { get; set; }
    [Id(3)] public string? Incarnation { get; set; }
    [Id(4)] public string? Epoch { get; set; }
    [Id(5)] public long Seq { get; set; }
}

[GenerateSerializer]
public sealed class LeaveRequest
{
    [Id(0)] public GatewayId Gateway { get; set; }
    [Id(1)] public ConnectionId Connection { get; set; }
    [Id(2)] public PlayerId Player { get; set; }
}

[GenerateSerializer]
public sealed class DetachRequest
{
    [Id(0)] public GatewayId Gateway { get; set; }
    [Id(1)] public List<ConnectionId> Connections { get; set; } = [];
}

[GenerateSerializer]
public sealed class RenewRequest
{
    [Id(0)] public GatewayRef Gateway { get; set; } = null!;
    [Id(1)] public List<RenewEntry> Connections { get; set; } = [];
}

[GenerateSerializer]
public sealed class RenewEntry
{
    [Id(0)] public ConnectionId Connection { get; set; }
    [Id(1)] public PlayerId Player { get; set; }
}

[GenerateSerializer]
public sealed class RenewReply
{
    /// <summary>False when the room does not exist (any more): the gateway tells its connections the room was lost.</summary>
    [Id(0)] public bool RoomExists { get; set; }
    /// <summary>Connections whose player is not a member (any more).</summary>
    [Id(1)] public List<ConnectionId> NotMembers { get; set; } = [];
    /// <summary>Snapshots for connections that were attached by this renewal.</summary>
    [Id(2)] public List<ConnectionSnapshot> Snapshots { get; set; } = [];
}

[GenerateSerializer]
public sealed class ConnectionSnapshot
{
    [Id(0)] public ConnectionId Connection { get; set; }
    [Id(1)] public SnapshotData Snapshot { get; set; } = null!;
}

[GenerateSerializer]
public enum ReplyStatus
{
    Ok = 0,
    Rejected = 1,
    Error = 2,
}

/// <summary>Outcome of a request to a room.</summary>
[GenerateSerializer]
public sealed class RoomReply
{
    [Id(0)] public ReplyStatus Status { get; set; }
    [Id(1)] public string? Code { get; set; }
    [Id(2)] public string? Reason { get; set; }
    /// <summary>Serialized return value of the handler, if any.</summary>
    [Id(3)] public byte[]? Data { get; set; }
    /// <summary>A snapshot the gateway must deliver to the calling connection before the reply.</summary>
    [Id(4)] public SnapshotData? Snapshot { get; set; }
    [Id(5)] public string? Incarnation { get; set; }

    public static RoomReply Ok(string? code = null, byte[]? data = null) => new() { Status = ReplyStatus.Ok, Code = code, Data = data };
    public static RoomReply Rejected(string code, string? reason = null) => new() { Status = ReplyStatus.Rejected, Code = code, Reason = reason };
    public static RoomReply Error(string code, string? reason = null) => new() { Status = ReplyStatus.Error, Code = code, Reason = reason };

    internal RoomReply WithoutSnapshot() => new() { Status = Status, Code = Code, Reason = Reason, Data = Data, Incarnation = Incarnation };
}

/// <summary>A per-viewer baseline of room state, "as of" a position in that player's event stream.</summary>
[GenerateSerializer]
public sealed class SnapshotData
{
    [Id(0)] public string Incarnation { get; set; } = "";
    [Id(1)] public string Epoch { get; set; } = "";
    [Id(2)] public long At { get; set; }
    [Id(3)] public byte[]? View { get; set; }
    [Id(4)] public List<MemberInfo> Members { get; set; } = [];
    [Id(5)] public MemberInfo You { get; set; } = null!;
}

[GenerateSerializer]
public sealed class MemberInfo
{
    [Id(0)] public string Player { get; set; } = "";
    [Id(1)] public string? Role { get; set; }
    [Id(2)] public PresenceStatus Status { get; set; }
}

/// <summary>Events from one room for the connections held by one gateway.</summary>
[GenerateSerializer]
public sealed class DeliveryBatch
{
    [Id(0)] public string Room { get; set; } = "";
    [Id(1)] public string Incarnation { get; set; } = "";
    [Id(2)] public string Epoch { get; set; } = "";
    [Id(3)] public List<DeliveryItem> Items { get; set; } = [];

    /// <summary>Counts up per room and gateway within an epoch, starting at 1, so a gateway can restore the order of batches.</summary>
    [Id(4)] public long Number { get; set; }
}

[GenerateSerializer]
public sealed class DeliveryItem
{
    [Id(0)] public string Type { get; set; } = "";
    [Id(1)] public byte[]? Data { get; set; }
    /// <summary>False for notices outside the per-player sequence (<c>$closed</c>).</summary>
    [Id(2)] public bool Sequenced { get; set; } = true;
    [Id(3)] public List<DeliveryTarget> Targets { get; set; } = [];
}

[GenerateSerializer]
public struct DeliveryTarget
{
    [Id(0)] public string ClientId { get; set; }
    [Id(1)] public long Seq { get; set; }
}
