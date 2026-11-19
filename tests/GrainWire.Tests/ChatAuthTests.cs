using ChatSample.Server.Auth;
using GrainWire.Server;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GrainWire.Tests;

public sealed class ChatAuthTests
{
    private const string Key = "a-test-key-that-is-at-least-32-bytes-long!";
    private const string OtherKey = "another-test-key-that-is-32-bytes-or-more!";

    private static JwtSettings Settings(string key = Key) => new() { Key = key };

    private static ConnectionAuthContext Context(string? token) => new() { ClientId = "c1", Token = token };

    [Fact]
    public async Task Tokens_round_trip_through_the_authenticator()
    {
        JwtSettings settings = Settings();
        string token = new TokenService(settings, TimeProvider.System).Issue("alice");

        AuthenticationResult result = await new JwtConnectionAuthenticator(settings)
            .AuthenticateAsync(Context(token), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Player.Value);
        Assert.Equal("alice", result.Claims["sub"]);
    }

    [Fact]
    public async Task The_authenticator_refuses_bad_tokens()
    {
        JwtSettings settings = Settings();
        var authenticator = new JwtConnectionAuthenticator(settings);

        string otherKeyToken = new TokenService(Settings(OtherKey), TimeProvider.System).Issue("alice");
        string expiredToken = new TokenService(settings, new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-8))).Issue("alice");

        foreach (string? token in new[] { null, "", "garbage", otherKeyToken, expiredToken })
        {
            AuthenticationResult result = await authenticator.AuthenticateAsync(Context(token), CancellationToken.None);
            Assert.False(result.Succeeded);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short-key")]
    public void A_missing_or_short_key_stops_startup(string? key)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null)
        {
            values["Jwt:Key"] = key;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Throws<InvalidOperationException>(() => JwtSettings.Load(configuration));
    }

    [Fact]
    public void A_long_enough_key_loads_with_default_issuer_and_audience()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = Key })
            .Build();

        JwtSettings settings = JwtSettings.Load(configuration);

        Assert.Equal("grainwire-chat-sample", settings.Issuer);
        Assert.Equal("grainwire-chat-sample", settings.Audience);
        Assert.Equal(7, settings.LifetimeDays);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
