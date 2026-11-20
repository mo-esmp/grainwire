using GrainWire.Server;

namespace GrainWire.Bench;

/// <summary>Accepts the user name as the player, like the development authenticator, and gives every connection three claims.</summary>
internal sealed class BenchAuthenticator : IConnectionAuthenticator
{
    public ValueTask<AuthenticationResult> AuthenticateAsync(ConnectionAuthContext context, CancellationToken cancellationToken)
    {
        Dictionary<string, string> claims = new()
        {
            ["tenant"] = "bench",
            ["tier"] = "standard",
            ["region"] = "eu-west",
        };
        return ValueTask.FromResult(AuthenticationResult.Success(new PlayerId(context.UserName!), claims));
    }
}
