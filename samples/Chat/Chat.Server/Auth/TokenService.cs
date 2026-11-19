using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatSample.Server.Auth;

/// <summary>Creates the signed tokens that sign a user in to the API and to GrainWire connections.</summary>
public sealed class TokenService(JwtSettings settings, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Makes a token for a username. The username is the token's "sub" claim.</summary>
    public string Issue(string username)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", username)]),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddDays(settings.LifetimeDays),
            SigningCredentials = new SigningCredentials(settings.SigningKey(), SecurityAlgorithms.HmacSha256),
        };

        return _handler.CreateToken(descriptor);
    }
}
