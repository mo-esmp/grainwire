namespace GrainWire.Client;

/// <summary>The state of a client's connection to the gateway.</summary>
public enum ConnectionState
{
    /// <summary>Not connected, and not trying to connect: before the first connection, and after the client is disposed.</summary>
    Disconnected,

    /// <summary>Connected, with the inbox subscribed.</summary>
    Connected,

    /// <summary>The connection was lost and the client is connecting again.</summary>
    Reconnecting,
}
