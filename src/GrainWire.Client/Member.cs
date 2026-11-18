namespace GrainWire.Client;

/// <summary>A member of a room, as the server reports it.</summary>
/// <param name="Player">The player id.</param>
/// <param name="Role">The role the room gave this player, if any.</param>
/// <param name="Status">Whether the player is reachable right now.</param>
public sealed record Member(string Player, string? Role, MemberStatus Status);
