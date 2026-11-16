using System.Text;
using System.Text.Json;
using GrainWire.Protocol;
using Orleans.TestingHost;

namespace GrainWire.Tests.Support;

/// <summary>One silo, shared by the room-core tests. Each test uses its own room ids.</summary>
public sealed class ClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        TestClusterBuilder builder = new(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await Cluster.DisposeAsync();

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => TestSilo.Configure(siloBuilder);
    }
}

[CollectionDefinition(Name)]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture>
{
    public const string Name = "cluster";
}

public sealed record Delivered(string ClientId, string Type, long Seq, bool Sequenced, string Epoch, string? Json, (int Epoch, long Batch, int Item) Order = default)
{
    public JsonElement Data => JsonDocument.Parse(Json ?? "null").RootElement;
}

/// <summary>Plays the part of a gateway in room-core tests: calls the room grain directly and records what it delivers.</summary>
public sealed class FakeGateway : IGatewayObserver
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly IGrainFactory _grains;
    private readonly List<Delivered> _delivered = [];
    private readonly List<string> _epochs = [];

    public FakeGateway(IGrainFactory grains)
    {
        _grains = grains;
        Ref = new GatewayRef { Id = new GatewayId(Guid.NewGuid().ToString("N")), Observer = grains.CreateObjectReference<IGatewayObserver>(this) };
    }

    public GatewayRef Ref { get; }

    public Task Deliver(DeliveryBatch batch)
    {
        lock (_delivered)
        {
            // One-way calls can arrive out of order; like the real gateway, order by the room's batch number.
            string epochKey = batch.Room + "|" + batch.Epoch;
            if (!_epochs.Contains(epochKey))
            {
                _epochs.Add(epochKey);
            }

            int epoch = _epochs.IndexOf(epochKey);
            for (int i = 0; i < batch.Items.Count; i++)
            {
                DeliveryItem item = batch.Items[i];
                foreach (DeliveryTarget target in item.Targets)
                {
                    _delivered.Add(new Delivered(target.ClientId, item.Type, target.Seq, item.Sequenced, batch.Epoch, item.Data is null ? null : Encoding.UTF8.GetString(item.Data), (epoch, batch.Number, i)));
                }
            }
        }

        return Task.CompletedTask;
    }

    public List<Delivered> For(string clientId)
    {
        lock (_delivered)
        {
            return [.. _delivered.Where(d => d.ClientId == clientId).OrderBy(d => d.Order)];
        }
    }

    public List<Delivered> All()
    {
        lock (_delivered)
        {
            return [.. _delivered];
        }
    }

    public async Task<List<Delivered>> WaitFor(string clientId, Func<List<Delivered>, bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            List<Delivered> events = For(clientId);
            if (condition(events))
            {
                return events;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met for {clientId}. Got: {string.Join(", ", events.Select(e => $"{e.Type}#{e.Seq}"))}");
            }

            await Task.Delay(20);
        }
    }

    public IRoomGrain Room(RoomAddress room) => _grains.GetGrain<IRoomGrain>(room.ToGrainId());

    public Task<RoomReply> Join(RoomAddress room, string player, string clientId, bool create = true, object? options = null, object? data = null, long generation = 1) =>
        Room(room).Join(new JoinRequest
        {
            Gateway = Ref,
            Connection = new ConnectionId(clientId, generation),
            Player = new PlayerId(player),
            Create = create,
            Options = Bytes(options),
            Data = Bytes(data),
        });

    public Task<RoomReply> Command(RoomAddress room, string player, string clientId, string name, object? payload = null, string? commandId = null, string? incarnation = null, long generation = 1) =>
        Room(room).Command(new CommandRequest
        {
            Gateway = Ref,
            Connection = new ConnectionId(clientId, generation),
            Player = new PlayerId(player),
            Name = name,
            CommandId = commandId,
            Incarnation = incarnation,
            Data = Bytes(payload),
        });

    public Task<RoomReply> Sync(RoomAddress room, string player, string clientId, string? incarnation, string? epoch, long seq, long generation = 1) =>
        Room(room).Sync(new SyncRequest
        {
            Gateway = Ref,
            Connection = new ConnectionId(clientId, generation),
            Player = new PlayerId(player),
            Incarnation = incarnation,
            Epoch = epoch,
            Seq = seq,
        });

    public Task Detach(RoomAddress room, string clientId, long generation = 1) =>
        Room(room).Detach(new DetachRequest { Gateway = Ref.Id, Connections = [new ConnectionId(clientId, generation)] });

    public Task<RenewReply> Renew(RoomAddress room, params (string ClientId, string Player)[] connections) =>
        Room(room).Renew(new RenewRequest
        {
            Gateway = Ref,
            Connections = [.. connections.Select(c => new RenewEntry { Connection = new ConnectionId(c.ClientId, 1), Player = new PlayerId(c.Player) })],
        });

    public static T Read<T>(byte[]? data) => JsonSerializer.Deserialize<T>(data!, s_json)!;

    public static JsonElement ReadJson(byte[]? data) => JsonDocument.Parse(data ?? "null"u8.ToArray()).RootElement;

    private static byte[]? Bytes(object? value) => value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value, s_json);
}

public static class Rooms
{
    public static RoomAddress New(string type) => new(type, Guid.NewGuid().ToString("N"));
}
