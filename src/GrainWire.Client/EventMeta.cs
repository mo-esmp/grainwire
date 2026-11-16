namespace GrainWire.Client;

/// <summary>Facts about an event besides its data.</summary>
/// <param name="Seq">The event's position in this player's stream for the room.</param>
public readonly record struct EventMeta(long Seq);
