using GrainWire.Client.Protocol;

namespace GrainWire.Client.Internal;

/// <summary>A request that has no reply yet. It keeps its command id for as long as it lives, so every re-send is recognised by the room.</summary>
internal sealed class PendingRequest(string commandId, RequestKind kind, string roomKey, string topic, byte[] payload, long deadline, Action<ReplyEnvelope?, Exception?> settled)
{
    public string CommandId { get; } = commandId;

    public RequestKind Kind { get; } = kind;

    public string RoomKey { get; } = roomKey;

    /// <summary>
    /// The room a command was sent through; null for every other kind of request. A later room with the same key is
    /// another room: the command does not belong to it.
    /// </summary>
    public Room? Room { get; init; }

    public string Topic { get; } = topic;

    public byte[] Payload { get; } = payload;

    /// <summary>When the request is given up, on the client's clock.</summary>
    public long Deadline { get; } = deadline;

    public long LastSentAt { get; set; }

    /// <summary>Called once, under the client's lock, with the reply or with the reason there is none.</summary>
    public Action<ReplyEnvelope?, Exception?> Settled { get; } = settled;
}
