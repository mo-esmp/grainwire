namespace GrainWire;

public sealed class JoinContext
{
    internal JoinContext(PlayerId player, IReadOnlyDictionary<string, string> claims, RoomPayload data, bool isCreator)
    {
        Player = player;
        Claims = claims;
        Data = data;
        IsCreator = isCreator;
    }

    public PlayerId Player { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
    /// <summary>App-defined join data (for example a requested role or an invite code).</summary>
    public RoomPayload Data { get; }
    public bool IsCreator { get; }
}
