using GrainWire;
using GrainWire.Server;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatSample.Server.Auth;

/// <summary>Checks the token a client sends when it connects and turns it into a player identity.</summary>
public sealed class JwtConnectionAuthenticator(JwtSettings settings) : IConnectionAuthenticator
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _parameters = settings.ToValidationParameters();

    /// <inheritdoc />
    public async ValueTask<AuthenticationResult> AuthenticateAsync(ConnectionAuthContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.Token))
        {
            return AuthenticationResult.Fail("A token is required.");
        }

        TokenValidationResult result = await _handler.ValidateTokenAsync(context.Token, _parameters);
        if (!result.IsValid)
        {
            return AuthenticationResult.Fail("The token is not valid.");
        }

        string? subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return AuthenticationResult.Fail("The token has no subject.");
        }

        Dictionary<string, string> claims = result.ClaimsIdentity.Claims
            .GroupBy(claim => claim.Type)
            .ToDictionary(group => group.Key, group => group.First().Value);
        return AuthenticationResult.Success(new PlayerId(subject), claims);
    }
}
