namespace GrainWire.Server;

/// <summary>Settings of the MQTT gateway. The defaults are a starting point; see docs/benchmarks.md.</summary>
public sealed class GrainWireGatewayOptions
{
    /// <summary>Path of the MQTT-over-WebSockets endpoint.</summary>
    public string Path { get; set; } = "/mqtt";

    /// <summary>Largest message a client may publish.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    public int MaxConnectionsPerPlayer { get; set; } = 5;

    /// <summary>Rooms one connection may be attached to at the same time.</summary>
    public int MaxRoomsPerConnection { get; set; } = 20;

    /// <summary>Sustained commands per second for one connection.</summary>
    public double CommandsPerSecond { get; set; } = 20;

    /// <summary>Commands one connection may send in a burst.</summary>
    public int CommandBurst { get; set; } = 40;

    /// <summary>Requests of one connection that may be waiting for a room at the same time.</summary>
    public int MaxPendingRequests { get; set; } = 32;

    /// <summary>Messages that may wait to be sent to one connection. A connection that falls further behind is disconnected.</summary>
    public int MaxOutgoingQueue { get; set; } = 1000;

    /// <summary>Rooms one player may create per minute through this gateway.</summary>
    public int RoomCreationsPerMinute { get; set; } = 10;

    /// <summary>How often the gateway renews its lease with every room it holds connections in.</summary>
    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The built-in development authenticator refuses to run in the Production environment unless this is set.</summary>
    public bool AllowDevelopmentAuthenticatorInProduction { get; set; }
}
