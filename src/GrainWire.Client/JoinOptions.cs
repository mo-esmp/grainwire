namespace GrainWire.Client;

/// <summary>How to join a room.</summary>
public sealed class JoinOptions
{
    /// <summary>Create the room if it does not exist. Default false.</summary>
    public bool Create { get; set; }

    /// <summary>Room settings, used only when the room is created. Serialized as JSON.</summary>
    public object? Options { get; set; }

    /// <summary>App-defined join data, such as a requested role or an invite code. Serialized as JSON, and sent again when the client rejoins after a reconnect.</summary>
    public object? Data { get; set; }
}
