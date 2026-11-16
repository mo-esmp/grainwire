namespace GrainWire;

/// <summary>A member of a room as handlers see it.</summary>
public sealed record RoomMember(PlayerId Player, string? Role, PresenceStatus Presence)
{
    public bool IsOnline => Presence == PresenceStatus.Online;
}
