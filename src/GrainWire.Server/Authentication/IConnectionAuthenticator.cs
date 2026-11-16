namespace GrainWire.Server;

/// <summary>Turns the credentials of a connecting client into a player identity. Register your own implementation in DI.</summary>
public interface IConnectionAuthenticator
{
    ValueTask<AuthenticationResult> AuthenticateAsync(ConnectionAuthContext context, CancellationToken cancellationToken);
}
