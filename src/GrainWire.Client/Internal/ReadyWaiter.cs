namespace GrainWire.Client.Internal;

/// <summary>Someone waiting for a room's first snapshot.</summary>
/// <param name="Deadline">When to give up, on the client's clock.</param>
/// <param name="CommandId">The join request this wait belongs to.</param>
/// <param name="Done">Called once, under the client's lock, with null when the snapshot arrived.</param>
internal sealed record ReadyWaiter(long Deadline, string CommandId, Action<Exception?> Done);
