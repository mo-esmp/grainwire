using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ChatSample.Server.Auth;

/// <summary>The settings for the sign-in tokens, read from the "Jwt" configuration section.</summary>
public sealed class JwtSettings
{
    private const int MinKeyBytes = 32;

    /// <summary>Who issues the tokens.</summary>
    public string Issuer { get; set; } = "grainwire-chat-sample";

    /// <summary>Who the tokens are for.</summary>
    public string Audience { get; set; } = "grainwire-chat-sample";

    /// <summary>The secret used to sign tokens. At least 32 bytes.</summary>
    public string Key { get; set; } = "";

    /// <summary>How many days a token stays valid.</summary>
    public int LifetimeDays { get; set; } = 7;

    /// <summary>Reads the settings and throws if the key is missing or too short, so startup fails early.</summary>
    public static JwtSettings Load(IConfiguration configuration)
    {
        var settings = new JwtSettings();
        configuration.GetSection("Jwt").Bind(settings);
        if (Encoding.UTF8.GetByteCount(settings.Key ?? "") < MinKeyBytes)
        {
            throw new InvalidOperationException(
                "Jwt:Key must be set to at least 32 bytes. Keep it in a secret store, not in source control.");
        }

        return settings;
    }

    /// <summary>The rules a token must pass: issuer, audience, lifetime and signature.</summary>
    public TokenValidationParameters ToValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(),
        NameClaimType = "sub",
    };

    /// <summary>The key as the token libraries want it.</summary>
    internal SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(Key));
}
