namespace GrainWire;

/// <summary>
/// A member of a room as handlers see it.
/// </summary>
/// <param name="Player">The member's player id.</param>
/// <param name="Role">The member's role, or null when it has none.</param>
/// <param name="Presence">Whether the member is online, reconnecting or gone.</param>
public sealed record RoomMember(PlayerId Player, string? Role, PresenceStatus Presence)
{
    /// <summary>
    /// True when the member has at least one connection attached.
    /// </summary>
    public bool IsOnline => Presence == PresenceStatus.Online;
}
