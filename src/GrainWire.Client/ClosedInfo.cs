namespace GrainWire.Client;

/// <summary>Why a room ended for this client, with the server's message when there is one.</summary>
public sealed record ClosedInfo(RoomClosedReason Reason, string? Message);
