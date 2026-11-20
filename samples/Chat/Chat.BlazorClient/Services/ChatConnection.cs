using GrainWire.Client;
using Microsoft.Extensions.Configuration;

namespace ChatSample.BlazorClient.Services;

/// <summary>
/// The one connection to the chat server that every page shares. It is opened the first time a room needs it and
/// kept while the user moves between pages. GrainWire.Client reconnects it by itself. It is closed on sign-out.
/// </summary>
public sealed class ChatConnection : IAsyncDisposable
{
    // MQTT 5 CONNACK reason codes with which the gateway refuses a token.
    private const int BadUserNameOrPassword = 134;
    private const int NotAuthorized = 135;

    private readonly IConfiguration _configuration;
    private readonly Session _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GrainWireClient? _client;
    private string? _connectedAs;

    /// <summary>Opens nothing yet: the connection is made by <see cref="GetAsync"/>.</summary>
    public ChatConnection(IConfiguration configuration, Session session)
    {
        _configuration = configuration;
        _session = session;
        session.Changed += OnSessionChanged;
    }

    /// <summary>Called when the connection goes up or down. It can be called on any thread.</summary>
    public event Action? StateChanged;

    /// <summary>The state of the connection; disconnected when there is none.</summary>
    public ConnectionState State => _client?.State ?? ConnectionState.Disconnected;

    /// <summary>
    /// Returns the connection, opening it first if needed. A different user gets a new connection, because the
    /// token is who they are to the server.
    /// </summary>
    /// <exception cref="ConnectException">
    /// The server could not be reached or refused the connection. When it refused the token, the user is signed out.
    /// </exception>
    /// <exception cref="InvalidOperationException">Nobody is signed in.</exception>
    public async Task<GrainWireClient> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            string name = _session.Username ?? throw new InvalidOperationException("Sign in first.");
            if (_client is not null && _connectedAs == name)
            {
                return _client;
            }

            await CloseAsync();

            GrainWireClient client;
            try
            {
                client = await GrainWireClient.ConnectAsync(new GrainWireClientOptions
                {
                    Url = new Uri(_configuration["Chat:MqttUrl"] ?? "ws://localhost:5300/mqtt"),

                    // Asked again before every reconnect, so the connection always uses the token the user has now.
                    AccessTokenProvider = _ => ValueTask.FromResult(_session.Token),
                });
            }
            catch (ConnectException error) when (error.ReasonCode is BadUserNameOrPassword or NotAuthorized)
            {
                // The token is expired or not accepted: retrying cannot help, so the user signs in again. A server that
                // is simply down has no reason code and does not end up here. The gate is held, and signing out wants
                // it to close the connection, so the sign-out is not awaited.
                _ = _session.SignOutAsync().AsTask();
                throw;
            }

            client.StateChanged += _ => StateChanged?.Invoke();
            _client = client;
            _connectedAs = name;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        _session.Changed -= OnSessionChanged;
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSessionChanged()
    {
        // Scheduled even when there is no connection yet: one may be opening right now, and the close waits for it
        // behind the gate, so it is never left running after sign-out.
        if (!_session.IsSignedIn)
        {
            _ = CloseWhenSignedOutAsync();
        }
    }

    private async Task CloseWhenSignedOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_session.IsSignedIn)
            {
                await CloseAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask CloseAsync()
    {
        GrainWireClient? client = _client;
        _client = null;
        _connectedAs = null;
        if (client is not null)
        {
            await client.DisposeAsync();
            StateChanged?.Invoke();
        }
    }
}
