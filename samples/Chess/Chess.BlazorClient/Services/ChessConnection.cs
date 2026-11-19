using GrainWire.Client;
using Microsoft.Extensions.Configuration;

namespace ChessFromScratch.BlazorClient.Services;

/// <summary>
/// The one connection to the chess server that every page shares. It is opened the first time a game needs it and
/// kept while the player moves between pages. GrainWire.Client reconnects it by itself.
/// </summary>
public sealed class ChessConnection(IConfiguration configuration, PlayerName playerName) : IAsyncDisposable
{
    private GrainWireClient? _client;
    private string? _connectedAs;

    /// <summary>
    /// Called when the connection goes up or down.
    /// </summary>
    public event Action? StateChanged;

    public ConnectionState State => _client?.State ?? ConnectionState.Disconnected;

    /// <summary>
    /// Returns the connection, opening it first if needed. A player who changed their name gets a new connection,
    /// because the name is who they are to the server.
    /// </summary>
    /// <exception cref="ConnectException">The server could not be reached or refused the connection.</exception>
    public async Task<GrainWireClient> GetAsync()
    {
        string name = await playerName.GetAsync();
        if (_client is not null && _connectedAs == name)
        {
            return _client;
        }

        await DisposeAsync();

        GrainWireClient client = await GrainWireClient.ConnectAsync(new GrainWireClientOptions
        {
            Url = new Uri(configuration["Chess:ServerUrl"] ?? "ws://localhost:5200/mqtt"),
            Username = name,
            AccessToken = "dev",
        });
        client.StateChanged += _ => StateChanged?.Invoke();
        _client = client;
        _connectedAs = name;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        GrainWireClient? client = _client;
        _client = null;
        _connectedAs = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }
}
