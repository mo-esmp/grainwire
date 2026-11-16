using System.Net;

namespace GrainWire.Server;

/// <summary>What the client sent when it connected.</summary>
public sealed class ConnectionAuthContext
{
    public required string ClientId { get; init; }
    /// <summary>The MQTT user name.</summary>
    public string? UserName { get; init; }
    /// <summary>The MQTT password field: normally the access token.</summary>
    public string? Token { get; init; }
    public EndPoint? RemoteEndPoint { get; init; }
    public bool IsSecureConnection { get; init; }
}
