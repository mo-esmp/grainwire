using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

public sealed class AuthenticationResult
{
    private AuthenticationResult(bool succeeded, PlayerId player, IReadOnlyDictionary<string, string>? claims, string? failureReason)
    {
        Succeeded = succeeded;
        Player = player;
        Claims = claims ?? new Dictionary<string, string>();
        FailureReason = failureReason;
    }

    public bool Succeeded { get; }
    public PlayerId Player { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
    public string? FailureReason { get; }

    public static AuthenticationResult Success(PlayerId player, IReadOnlyDictionary<string, string>? claims = null) => new(true, player, claims, null);
    public static AuthenticationResult Fail(string reason) => new(false, default, null, reason);
}

/// <summary>Turns the credentials of a connecting client into a player identity. Register your own implementation in DI.</summary>
public interface IConnectionAuthenticator
{
    ValueTask<AuthenticationResult> AuthenticateAsync(ConnectionAuthContext context, CancellationToken cancellationToken);
}

/// <summary>
/// For development only: trusts the MQTT user name as the player id, with no password check.
/// Anyone can be anyone. Replace it before going live.
/// </summary>
public sealed class DevelopmentAuthenticator : IConnectionAuthenticator
{
    private readonly ILogger<DevelopmentAuthenticator> _log;
    private int _warned;

    public DevelopmentAuthenticator(IHostEnvironment environment, GrainWireGatewayOptions options, ILogger<DevelopmentAuthenticator> log)
    {
        _log = log;
        if (environment.IsProduction() && !options.AllowDevelopmentAuthenticatorInProduction)
        {
            throw new InvalidOperationException(
                "GrainWire's DevelopmentAuthenticator trusts any user name and must not run in Production. " +
                "Register your own IConnectionAuthenticator, or set AllowDevelopmentAuthenticatorInProduction if you really mean it.");
        }
    }

    public ValueTask<AuthenticationResult> AuthenticateAsync(ConnectionAuthContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _log.LogWarning("GrainWire is using the DevelopmentAuthenticator: every client is trusted to be whoever its user name says. Do not use this in production.");
        }

        return ValueTask.FromResult(string.IsNullOrWhiteSpace(context.UserName)
            ? AuthenticationResult.Fail("A user name is required.")
            : AuthenticationResult.Success(new PlayerId(context.UserName)));
    }
}
