namespace GrainWire;

/// <summary>Why a member left the room.</summary>
public enum LeaveReason
{
    /// <summary>The reconnect grace period ran out.</summary>
    GraceExpired = 0,
    /// <summary>A handler removed the member.</summary>
    Removed = 1,
}
