using System.Text.Json;
using GrainWire.Client.Internal;
using MQTTnet;

namespace GrainWire.Client;

/// <summary>How a <see cref="GrainWireClient"/> connects and behaves.</summary>
public sealed class GrainWireClientOptions
{
    /// <summary>The gateway's WebSocket endpoint, for example <c>wss://example.com/mqtt</c>.</summary>
    public required Uri Url { get; set; }

    /// <summary>Access token, sent as the MQTT password. Ignored when <see cref="AccessTokenProvider"/> is set.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Returns the access token. Called before the first connection and again before every reconnect, so an expired token can be replaced.</summary>
    public Func<CancellationToken, ValueTask<string?>>? AccessTokenProvider { get; set; }

    /// <summary>MQTT user name. What it means is up to the server's authenticator. Default <c>token</c>.</summary>
    public string Username { get; set; } = "token";

    /// <summary>
    /// The MQTT client id: 16 to 128 characters of A-Z, a-z, 0-9, '-' or '_'. It names this client's inbox, so never
    /// share it. Defaults to a random id, which the client keeps when it reconnects.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>A room that has been silent this long is checked against the server. Default 15 s.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for a missing event before asking for a snapshot. Default 250 ms.</summary>
    public TimeSpan GapWait { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a request waits for a reply before failing with <see cref="OutcomeUnknownException"/>. Default 15 s.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A request without a reply is re-sent this often, with the same command id. Default 3 s.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>First reconnect delay; it doubles up to <see cref="MaxReconnectDelay"/>. Default 500 ms.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Default 15 s.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long one connection attempt may take. Default 10 s.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How command data, join data, views, events and results are converted to and from JSON. Defaults to
    /// <see cref="JsonSerializerDefaults.Web"/> (camelCase names), which matches a server that uses its default serializer.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>Extra MQTT settings, such as TLS options or WebSocket headers. Called for every connection attempt, after the client has applied its own settings.</summary>
    public Action<MqttClientOptionsBuilder>? ConfigureMqtt { get; set; }

    /// <summary>
    /// How many handler calls may wait for the app. Handlers run one at a time, so calls queue up when messages arrive
    /// faster than the handlers return. Once this many wait, a room stops queueing calls for its events: it skips
    /// ahead. When the app has caught up, the room asks the server for a snapshot and carries on from there, as it does
    /// after a reconnect: the snapshot handler is called with the state as it is now, and the events in between are
    /// not delivered. Defaults to 1,000.
    /// </summary>
    public int MaxPendingHandlerCalls { get; set; } = 1000;

    /// <summary>
    /// Called when one of the app's handlers throws. The client carries on with the next handler. Without this, such
    /// exceptions are ignored.
    /// </summary>
    public Action<Exception>? OnHandlerError { get; set; }

    /// <summary>Returns a checked copy, so later changes to these options do not affect a running client.</summary>
    internal GrainWireClientOptions Validated()
    {
        GrainWireClientOptions copy = (GrainWireClientOptions)MemberwiseClone();
        ArgumentNullException.ThrowIfNull(copy.Url, nameof(Url));
        if (!copy.Url.IsAbsoluteUri || copy.Url.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("The URL must be an absolute ws:// or wss:// address.", nameof(Url));
        }

        copy.ClientId ??= Guid.NewGuid().ToString("N");
        if (!Wire.IsClientId(copy.ClientId))
        {
            throw new ArgumentException("The client id must be 16 to 128 characters of A-Z, a-z, 0-9, '-' or '_'.", nameof(ClientId));
        }

        ArgumentException.ThrowIfNullOrEmpty(copy.Username, nameof(Username));
        Positive(copy.SyncInterval, nameof(SyncInterval));
        Positive(copy.GapWait, nameof(GapWait));
        Positive(copy.RequestTimeout, nameof(RequestTimeout));
        Positive(copy.RetryInterval, nameof(RetryInterval));
        Positive(copy.ReconnectDelay, nameof(ReconnectDelay));
        Positive(copy.ConnectTimeout, nameof(ConnectTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(copy.MaxPendingHandlerCalls, 1, nameof(MaxPendingHandlerCalls));
        if (copy.MaxReconnectDelay < copy.ReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectDelay), "Must not be shorter than ReconnectDelay.");
        }

        copy.SerializerOptions ??= JsonSerializerOptions.Web;
        return copy;
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, "Must be greater than zero.");
        }
    }
}
