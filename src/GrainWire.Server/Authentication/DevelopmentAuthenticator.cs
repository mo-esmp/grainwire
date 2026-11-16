using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrainWire.Server;

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
