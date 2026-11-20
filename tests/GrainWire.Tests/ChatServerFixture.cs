using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChatSample.Server;
using ChatSample.Server.Api;
using GrainWire.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrainWire.Tests;

/// <summary>
/// Runs the chat sample's server in this process, on fresh ports and a temporary database. Every setting the
/// server needs is passed in memory, so the tests do not depend on which appsettings.json is in the test folder.
/// </summary>
public sealed class ChatServerFixture : IAsyncLifetime
{
    private static int _names;

    private readonly int _httpPort = Ports.Next();
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "chat-test-" + Guid.NewGuid().ToString("N") + ".db");
    private WebApplication? _app;

    /// <summary>The server's WebSocket address for GrainWire connections.</summary>
    public Uri MqttUrl => new($"ws://127.0.0.1:{_httpPort}/mqtt");

    /// <summary>The services of the running server.</summary>
    public IServiceProvider Services => _app!.Services;

    /// <summary>A name nobody has used yet on this server, so tests can share it.</summary>
    public static string NewName(string prefix) => prefix + Interlocked.Increment(ref _names);

    public async Task InitializeAsync()
    {
        _app = ChatServerHost.Build([], builder =>
        {
            builder.Environment.EnvironmentName = Environments.Development;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Urls"] = $"http://127.0.0.1:{_httpPort}",
                ["Jwt:Key"] = "test-key-test-key-test-key-test-key-0123456789",
                ["Chat:Database"] = $"Data Source={_databasePath}",
                ["Chat:ClientOrigins:0"] = "http://localhost:5302",
                ["Orleans:SiloPort"] = Ports.Next().ToString(),
                ["Orleans:GatewayPort"] = Ports.Next().ToString(),
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Error);
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
            try
            {
                await _app.StopAsync(cts.Token);
            }
            catch
            {
                // Stopping timed out: nothing more to do in a test.
            }

            await _app.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // The temp folder is cleaned up eventually.
        }
    }

    /// <summary>A client for the server's API, signed in with the token when one is given.</summary>
    public HttpClient Http(string? token = null)
    {
        HttpClient http = new() { BaseAddress = new Uri($"http://127.0.0.1:{_httpPort}") };
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http;
    }

    /// <summary>Registers a user with the password "password1" and returns the token.</summary>
    public async Task<string> SignUpAsync(string name)
    {
        using HttpClient http = Http();
        HttpResponseMessage response = await http.PostAsJsonAsync("/api/auth/register", new Credentials(name, "password1"));
        response.EnsureSuccessStatusCode();
        SignedIn? signedIn = await response.Content.ReadFromJsonAsync<SignedIn>();
        return signedIn!.Token;
    }
}

[CollectionDefinition(Name)]
public sealed class ChatServerCollection : ICollectionFixture<ChatServerFixture>
{
    public const string Name = "ChatServer";
}
