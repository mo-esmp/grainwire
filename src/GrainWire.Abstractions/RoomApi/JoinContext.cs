namespace GrainWire;

/// <summary>
/// What a room sees when a player asks to join it, so it can accept or reject the request.
/// </summary>
public sealed class JoinContext
{
    internal JoinContext(PlayerId player, IReadOnlyDictionary<string, string> claims, RoomPayload data, bool isCreator)
    {
        Player = player;
        Claims = claims;
        Data = data;
        IsCreator = isCreator;
    }

    /// <summary>
    /// The player asking to join.
    /// </summary>
    public PlayerId Player { get; }

    /// <summary>
    /// Claims of the player's authenticated connection.
    /// </summary>
    public IReadOnlyDictionary<string, string> Claims { get; }

    /// <summary>
    /// App-defined join data (for example a requested role or an invite code).
    /// </summary>
    public RoomPayload Data { get; }

    /// <summary>
    /// True when this join is the one that creates the room.
    /// </summary>
    public bool IsCreator { get; }
}
