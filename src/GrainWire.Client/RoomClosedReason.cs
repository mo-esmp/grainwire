namespace GrainWire.Client;

/// <summary>Why a room ended for this client.</summary>
public enum RoomClosedReason
{
    /// <summary>The room was closed on the server.</summary>
    Closed,

    /// <summary>This player was kicked.</summary>
    Kicked,

    /// <summary>This player is no longer a member.</summary>
    Removed,

    /// <summary>The room's state was lost on the server.</summary>
    Lost,

    /// <summary>This client left the room itself. Never sent by the server.</summary>
    Left,
}
