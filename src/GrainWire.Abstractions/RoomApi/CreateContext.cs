namespace GrainWire;

/// <summary>
/// What a room sees when someone asks to create it, so it can allow or deny the request.
/// </summary>
public sealed class CreateContext
{
    internal CreateContext(PlayerId creator, IReadOnlyDictionary<string, string> claims, RoomPayload options)
    {
        Creator = creator;
        Claims = claims;
        Options = options;
    }

    /// <summary>
    /// The player asking to create the room.
    /// </summary>
    public PlayerId Creator { get; }

    /// <summary>
    /// Claims of the creator's authenticated connection.
    /// </summary>
    public IReadOnlyDictionary<string, string> Claims { get; }

    /// <summary>
    /// Room settings chosen by the creator (for example a time control).
    /// </summary>
    public RoomPayload Options { get; }
}
