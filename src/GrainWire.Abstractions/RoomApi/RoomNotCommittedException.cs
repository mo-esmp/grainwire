namespace GrainWire;

/// <summary>
/// Thrown when a change could not be saved and was rolled back.
/// </summary>
public sealed class RoomNotCommittedException() : Exception("The change could not be saved and was rolled back.");
