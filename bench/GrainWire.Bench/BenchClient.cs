using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace GrainWire.Bench;

/// <summary>A minimal GrainWire client for load generation: joins a room, sends commands, timestamps what arrives.</summary>
public sealed class BenchClient : IAsyncDisposable
{
    private static readonly MqttClientFactory s_factory = new();
    private readonly IMqttClient _mqtt = s_factory.CreateMqttClient();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _replies = new();
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private readonly string _player;
    private TaskCompletionSource _snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _commandCounter;
    private long _said;
    private readonly SequenceGaps _gaps = new();
    private static long s_received;

    /// <summary>Messages received by all clients so far.</summary>
    public static long Received => Interlocked.Read(ref s_received);

    /// <summary>Waits until no client has received anything for a second. Returns how long that took.</summary>
    public static async Task<TimeSpan> WaitForQuietAsync()
    {
        Stopwatch watch = Stopwatch.StartNew();
        long last = Received;
        TimeSpan quietSince = watch.Elapsed;
        while (watch.Elapsed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(100);
            long now = Received;
            if (now != last)
            {
                last = now;
                quietSince = watch.Elapsed;
            }
            else if (watch.Elapsed - quietSince >= TimeSpan.FromSeconds(1))
            {
                return quietSince;
            }
        }

        return watch.Elapsed;
    }

    public BenchClient(string player, Stats events)
    {
        _player = player;
        _mqtt.DisconnectedAsync += _ => Disconnected?.Invoke() ?? Task.CompletedTask;
        _mqtt.ApplicationMessageReceivedAsync += e =>
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref s_received);
            Utf8JsonReader reader = new(e.ApplicationMessage.Payload);
            string? type = null, command = null, room = null;
            long sentAt = 0, seq = 0;
            bool ok = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("t"u8)) { reader.Read(); type = reader.GetString(); }
                else if (reader.ValueTextEquals("cmd"u8)) { reader.Read(); command = reader.GetString(); }
                else if (reader.ValueTextEquals("status"u8)) { reader.Read(); ok = reader.ValueTextEquals("ok"u8); }
                else if (reader.ValueTextEquals("ts"u8)) { reader.Read(); sentAt = reader.GetInt64(); }
                else if (reader.CurrentDepth == 1 && reader.ValueTextEquals("room"u8)) { reader.Read(); room = reader.GetString(); }
                else if (reader.CurrentDepth == 1 && reader.ValueTextEquals("seq"u8)) { reader.Read(); seq = reader.GetInt64(); }
                else if (reader.ValueTextEquals("pad"u8) || reader.ValueTextEquals("blob"u8)) { reader.Read(); }
            }

            if (type == "$reply" && command is not null && _replies.TryRemove(command, out TaskCompletionSource<bool>? waiting))
            {
                waiting.TrySetResult(ok);
            }
            else if (type == "$snapshot")
            {
                _snapshot.TrySetResult();
            }
            else if (type == "said")
            {
                Interlocked.Increment(ref _said);
                if (room is not null && seq > 0)
                {
                    _gaps.Observe(room, seq);
                }

                if (sentAt != 0)
                {
                    events.Record(Stopwatch.GetElapsedTime(sentAt, now));
                }
            }

            return Task.CompletedTask;
        };
    }

    public Func<Task>? Disconnected { get; set; }

    /// <summary>"said" events this client has received.</summary>
    public long SaidReceived => Interlocked.Read(ref _said);

    /// <summary>"said" events this client never saw although later ones of the same room arrived.</summary>
    public long SaidGaps => _gaps.Total;

    /// <summary>Waits until every client has received at least <paramref name="expectedEach"/> "said" events, or the time is up.</summary>
    public static async Task WaitForSaidAsync(IReadOnlyList<BenchClient?> clients, long expectedEach, TimeSpan limit)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit && clients.Any(client => client is null || client.SaidReceived < expectedEach))
        {
            await Task.Delay(100);
        }
    }

    public async Task ConnectAsync(string url)
    {
        MqttClientOptions options = new MqttClientOptionsBuilder()
            .WithWebSocketServer(o => o.WithUri(url))
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(_clientId)
            .WithCredentials(_player, "bench")
            .WithCleanStart(true)
            .WithTimeout(TimeSpan.FromSeconds(60))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .Build();
        MqttClientConnectResult result = await _mqtt.ConnectAsync(options);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"Connect refused: {result.ResultCode} {result.ReasonString}");
        }

        await _mqtt.SubscribeAsync($"clients/{_clientId}/inbox");
    }

    /// <summary>Joins and waits for the snapshot.</summary>
    public async Task JoinAsync(string room, bool create, int snapshotBytes)
    {
        _snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string body = create ? $",\"create\":true,\"options\":{{\"snapshotBytes\":{snapshotBytes}}}" : "";
        if (!await RequestAsync($"rooms/{room}/sys/join", body))
        {
            throw new InvalidOperationException($"{_player} could not join {room}");
        }

        await _snapshot.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Sends a "say" command stamped with the current time; returns when the reply arrives.</summary>
    public Task<bool> SayAsync(string room, string pad) =>
        RequestAsync($"rooms/{room}/cmd/say", $",\"d\":{{\"ts\":{Stopwatch.GetTimestamp()},\"pad\":\"{pad}\"}}");

    private async Task<bool> RequestAsync(string topic, string bodyAfterCommandId)
    {
        string command = $"{_clientId[..8]}-{Interlocked.Increment(ref _commandCounter)}";
        TaskCompletionSource<bool> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[command] = waiting;
        byte[] payload = Encoding.UTF8.GetBytes($"{{\"cmd\":\"{command}\"{bodyAfterCommandId}}}");
        await _mqtt.PublishAsync(new MqttApplicationMessage { Topic = topic, PayloadSegment = payload, QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce });
        try
        {
            return await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            _replies.TryRemove(command, out _);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Disconnected = null;
        try
        {
            if (_mqtt.IsConnected)
            {
                await _mqtt.DisconnectAsync();
            }
        }
        catch
        {
            // The server may already be gone.
        }

        _mqtt.Dispose();
    }
}

/// <summary>Collects latency samples and reports percentiles.</summary>
public sealed class Stats
{
    private readonly ConcurrentBag<double> _samples = [];

    public void Record(TimeSpan elapsed) => _samples.Add(elapsed.TotalMilliseconds);

    public int Count => _samples.Count;

    public (double P50, double P95, double P99, double Max) Percentiles()
    {
        double[] sorted = [.. _samples];
        if (sorted.Length == 0)
        {
            return (0, 0, 0, 0);
        }

        Array.Sort(sorted);
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
        return (At(0.50), At(0.95), At(0.99), sorted[^1]);
    }
}
