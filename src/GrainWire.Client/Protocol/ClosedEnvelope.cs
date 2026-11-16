namespace GrainWire.Client.Protocol;

/// <summary>A notice that the room has ended for this connection. Not sequenced.</summary>
public sealed record ClosedEnvelope : InboxEnvelope
{
    /// <summary>The room incarnation (<c>ri</c>).</summary>
    public string? Incarnation { get; init; }

    public required RoomClosedReason Reason { get; init; }

    public string? Message { get; init; }
}
