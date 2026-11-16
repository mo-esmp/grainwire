using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GrainWire.Tests.Support;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace GrainWire.Tests.Client;

/// <summary>What a client published: the topic and the JSON object.</summary>
public sealed record Published(string Topic, JsonElement Body)
{
    public string CommandId => Body.GetProperty("cmd").GetString()!;
}

/// <summary>
/// A bare MQTT broker that plays the gateway by hand: it records what the client publishes and sends to the client's
/// inbox exactly what a test tells it to, in exactly that order. That makes the orderings a real server produces only
/// now and then (a reply before its events, a closure notice before the reply) repeatable.
/// </summary>
public sealed class ScriptedGateway : IAsyncDisposable
{
    public const string Room = "counter/r1";
    private const string Sender = "$script";
    private readonly MqttServer _server;
    private readonly List<Published> _received = [];
    private int _consumed;

    private ScriptedGateway(MqttServer server, int port)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; }

    public static async Task<ScriptedGateway> StartAsync()
    {
        int port = Ports.Next();
        MqttServerOptions options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
            .WithDefaultEndpointPort(port)
            .Build();
        MqttServer server = new MqttServerFactory().CreateMqttServer(options);
        ScriptedGateway gateway = new(server, port);
        server.InterceptingPublishAsync += e =>
        {
            if (e.ClientId != Sender)
            {
                // Like the real gateway: nothing a client publishes is routed to anyone.
                e.ProcessPublish = false;
                string json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                lock (gateway._received)
                {
                    gateway._received.Add(new Published(e.ApplicationMessage.Topic, JsonDocument.Parse(json).RootElement));
                }
            }

            return Task.CompletedTask;
        };
        await server.StartAsync();
        return gateway;
    }

    /// <summary>
    /// The next message the client published to <c>rooms/{Room}/{path}</c> that no earlier call returned. Messages
    /// to other paths that came before it are skipped for good.
    /// </summary>
    public async Task<Published> Next(string path, int timeoutMs = 5000)
    {
        string topic = $"rooms/{Room}/{path}";
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_received)
            {
                while (_consumed < _received.Count)
                {
                    Published next = _received[_consumed++];
                    if (next.Topic == topic)
                    {
                        return next;
                    }
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The client did not publish to {topic}. It published: {string.Join(" | ", All().Select(p => p.Topic + " " + p.Body.GetRawText()))}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Everything the client published so far.</summary>
    public List<Published> All()
    {
        lock (_received)
        {
            return [.. _received];
        }
    }

    public Task Send(string clientId, JsonObject message) => Send(clientId, message.ToJsonString());

    public Task Send(string clientId, string json) =>
        _server.InjectApplicationMessage(
            new InjectedMqttApplicationMessage(new MqttApplicationMessage
            {
                Topic = $"clients/{clientId}/inbox",
                PayloadSegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce,
            })
            {
                SenderClientId = Sender,
            },
            CancellationToken.None);

    public Task Snapshot(string clientId, long at, int value, string se = "e1", string ri = "i1")
    {
        JsonObject you = new() { ["player"] = "alice", ["role"] = "owner", ["status"] = "online" };
        return Send(clientId, new JsonObject
        {
            ["t"] = "$snapshot",
            ["room"] = Room,
            ["ri"] = ri,
            ["se"] = se,
            ["at"] = at,
            ["d"] = new JsonObject { ["value"] = value },
            ["members"] = new JsonArray(you.DeepClone()),
            ["you"] = you,
        });
    }

    public Task Event(string clientId, long seq, int value, string se = "e1", string ri = "i1") => Send(clientId, new JsonObject
    {
        ["t"] = "changed",
        ["room"] = Room,
        ["ri"] = ri,
        ["se"] = se,
        ["seq"] = seq,
        ["d"] = new JsonObject { ["value"] = value },
    });

    /// <param name="d">The handler's return value, as JSON text.</param>
    public Task Reply(string clientId, string commandId, string status = "ok", string? code = null, string? d = null)
    {
        JsonObject reply = new() { ["t"] = "$reply", ["room"] = Room, ["cmd"] = commandId, ["status"] = status };
        if (code is not null)
        {
            reply["code"] = code;
        }

        reply["ri"] = "i1";
        if (d is not null)
        {
            reply["d"] = JsonNode.Parse(d);
        }

        return Send(clientId, reply);
    }

    public Task Closed(string clientId, string reason, string message) => Send(clientId, new JsonObject
    {
        ["t"] = "$closed",
        ["room"] = Room,
        ["ri"] = "i1",
        ["d"] = new JsonObject { ["reason"] = reason, ["message"] = message },
    });

    /// <summary>Cuts a client's connection from the server side, like a network failure.</summary>
    public Task Disconnect(string clientId) =>
        _server.DisconnectClientAsync(clientId, new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }
}
