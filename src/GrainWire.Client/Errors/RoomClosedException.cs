namespace GrainWire.Client;

/// <summary>The room is closed for this client.</summary>
public sealed class RoomClosedException(RoomClosedReason reason)
    : GrainWireClientException($"The room is closed ({reason.ToString().ToLowerInvariant()}).")
{
    public RoomClosedReason Reason { get; } = reason;
}
