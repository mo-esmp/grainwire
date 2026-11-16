namespace GrainWire.Client;

/// <summary>Whether a member of a room is reachable right now.</summary>
public enum MemberStatus
{
    /// <summary>At least one of the player's connections is in the room.</summary>
    Online,

    /// <summary>The player has no connection in the room, and the room is still holding their seat.</summary>
    Reconnecting,

    /// <summary>The player is no longer a member.</summary>
    Left,
}
