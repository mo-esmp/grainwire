namespace GrainWire;

public sealed class CreateContext
{
    internal CreateContext(PlayerId creator, IReadOnlyDictionary<string, string> claims, RoomPayload options)
    {
        Creator = creator;
        Claims = claims;
        Options = options;
    }

    public PlayerId Creator { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
    /// <summary>Room settings chosen by the creator (for example a time control).</summary>
    public RoomPayload Options { get; }
}
