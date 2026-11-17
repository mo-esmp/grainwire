namespace GrainWire.Client.Protocol;

/// <summary>What a client has processed so far of a room's stream. Sent with a sync request.</summary>
/// <param name="Incarnation">The room incarnation (<c>ri</c>), or null before the first snapshot.</param>
/// <param name="Epoch">The stream epoch (<c>se</c>), or null before the first snapshot.</param>
/// <param name="Seq">The last position processed.</param>
public readonly record struct StreamPosition(string? Incarnation, string? Epoch, long Seq);
