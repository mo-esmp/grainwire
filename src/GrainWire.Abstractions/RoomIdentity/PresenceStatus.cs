namespace GrainWire;

/// <summary>
/// Presence of a member in a room.
/// </summary>
[GenerateSerializer]
public enum PresenceStatus
{
    /// <summary>
    /// At least one connection is attached.
    /// </summary>
    Online = 0,

    /// <summary>
    /// No connection is attached; the grace period is running.
    /// </summary>
    Reconnecting = 1,

    /// <summary>
    /// The member is gone (grace period expired, left, or was removed).
    /// </summary>
    Left = 2,
}
