using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace GrainWire.Tests.Support;

/// <summary>A bare MQTT client that speaks the GrainWire protocol by hand, for testing the server without the TypeScript client.</summary>
public sealed class TestClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IMqttClient _client;
    private readonly List<JsonElement> _inbox = new();

    private TestClient(IMqttClient client, string clientId, string player)
    {
        _client = client;
        ClientId = clientId;
        Player = player;
    }

    public string ClientId { get; }
    public string Player { get; }
    public MqttClientConnectResult ConnectResult { get; private set; } = null!;
    public bool IsConnected => _client.IsConnected;
    public string InboxTopic => $"clients/{ClientId}/inbox";

    public static string NewClientId() => Guid.NewGuid().ToString("N");

    public static async Task<TestClient> ConnectAsync(string url, string player, string? clientId = null, Action<MqttClientOptionsBuilder>? configure = null, bool subscribe = true)
    {
        clientId ??= NewClientId();
        var mqtt = new MqttClientFactory().CreateMqttClient();
        var client = new TestClient(mqtt, clientId, player);
        mqtt.ApplicationMessageReceivedAsync += e =>
        {
            var element = JsonDocument.Parse(e.ApplicationMessage.ConvertPayloadToString() ?? "null").RootElement;
            lock (client._inbox) client._inbox.Add(element);
            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithWebSocketServer(o => o.WithUri(url))
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(clientId)
            .WithCredentials(player, "token")
            .WithCleanStart(true)
            .WithTimeout(TimeSpan.FromSeconds(10));
        configure?.Invoke(options);
        client.ConnectResult = await mqtt.ConnectAsync(options.Build());
        if (client.ConnectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            mqtt.Dispose();
            throw new ConnectRefusedException(client.ConnectResult.ResultCode, client.ConnectResult.ReasonString);
        }

        if (subscribe)
        {
            var result = await mqtt.SubscribeAsync(client.InboxTopic);
            if (result.Items.First().ResultCode != MqttClientSubscribeResultCode.GrantedQoS0) throw new InvalidOperationException("Inbox subscription refused: " + result.Items.First().ResultCode);
        }

        return client;
    }

    public Task<MqttClientSubscribeResult> Subscribe(string topic) => _client.SubscribeAsync(topic);

    public Task<MqttClientPublishResult> Publish(string topic, string payload, bool retain = false) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

    /// <summary>Publishes a request and waits for its <c>$reply</c>.</summary>
    public async Task<JsonElement> Request(string room, string path, Dictionary<string, object?>? body = null, string? commandId = null, int timeoutMs = 10000)
    {
        commandId ??= Guid.NewGuid().ToString("N");
        body ??= new();
        body["cmd"] = commandId;
        var published = await Publish($"rooms/{room}/{path}", JsonSerializer.Serialize(body, Json));
        if (!published.IsSuccess) throw new InvalidOperationException($"Publish refused: {published.ReasonCode} {published.ReasonString}");
        var messages = await WaitFor(all => all.Any(m => IsReply(m, commandId)), timeoutMs);
        return messages.First(m => IsReply(m, commandId));
    }

    public Task<JsonElement> Join(string room, bool create = true, object? options = null, object? data = null) =>
        Request(room, "sys/join", new() { ["create"] = create, ["options"] = options, ["data"] = data });

    public Task<JsonElement> Command(string room, string name, object? d = null, string? ri = null, string? commandId = null) =>
        Request(room, "cmd/" + name, new() { ["d"] = d, ["ri"] = ri }, commandId);

    public Task<JsonElement> Sync(string room, string? ri, string? se, long seq) =>
        Request(room, "sys/sync", new() { ["ri"] = ri, ["se"] = se, ["seq"] = seq });

    public List<JsonElement> Inbox()
    {
        lock (_inbox) return _inbox.ToList();
    }

    /// <summary>Messages for one room, optionally of one type.</summary>
    public List<JsonElement> Of(string room, string? type = null) =>
        Inbox().Where(m => m.GetProperty("room").GetString() == room && (type is null || m.GetProperty("t").GetString() == type)).ToList();

    public async Task<List<JsonElement>> WaitFor(Func<List<JsonElement>, bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var inbox = Inbox();
            if (condition(inbox)) return inbox;
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met. Inbox: " + string.Join(" | ", inbox.Select(m => m.GetRawText())));
            }

            await Task.Delay(20);
        }
    }

    public Task<List<JsonElement>> WaitForType(string room, string type, int count = 1, int timeoutMs = 10000) =>
        WaitFor(all => all.Count(m => m.GetProperty("room").GetString() == room && m.GetProperty("t").GetString() == type) >= count, timeoutMs)
            .ContinueWith(_ => Of(room, type), TaskScheduler.Default);

    /// <summary>Closes the connection without a DISCONNECT packet, like a browser tab losing its network.</summary>
    public void Abort() => _client.Dispose();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync();
        }
        catch
        {
            // Already gone.
        }

        _client.Dispose();
    }

    private static bool IsReply(JsonElement message, string commandId) =>
        message.GetProperty("t").GetString() == "$reply" && message.TryGetProperty("cmd", out var cmd) && cmd.GetString() == commandId;
}

public sealed class ConnectRefusedException(MqttClientConnectResultCode code, string? reason) : Exception($"{code}: {reason}")
{
    public MqttClientConnectResultCode Code { get; } = code;
    public string? Reason { get; } = reason;
}

public static class JsonElementExtensions
{
    public static string? Str(this JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;

    public static long Num(this JsonElement element, string property) => element.GetProperty(property).GetInt64();

    public static string Status(this JsonElement reply) => reply.GetProperty("status").GetString()!;

    public static string? Code(this JsonElement reply) => reply.Str("code");
}
