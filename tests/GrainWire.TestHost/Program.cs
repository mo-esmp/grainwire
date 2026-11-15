using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GrainWire.Server;
using GrainWire.Server.Internal;
using GrainWire.Tests.Support;
using MQTTnet.Protocol;
using MQTTnet.Server;

// A real GrainWire server for the TypeScript client's integration tests and for manual experiments.
// It hosts the test rooms and adds HTTP endpoints that inject faults (lost messages, dropped connections,
// failing storage). Never deploy this.
//
//   dotnet run --project tests/GrainWire.TestHost -- --port 5080

var port = 5080;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = Environments.Development });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering(FreePort(), FreePort(), null, "gw-testhost-" + port, "gw-testhost-" + port);
    TestSilo.Configure(silo);
});
builder.Services.AddGrainWireGateway(options =>
{
    options.LeaseRenewInterval = TimeSpan.FromMilliseconds(300);
    options.CommandsPerSecond = 10_000;
    options.CommandBurst = 10_000;
    options.RoomCreationsPerMinute = 1_000_000;
    options.MaxConnectionsPerPlayer = 1000;
});

var app = builder.Build();
app.MapGrainWire();

// How many upcoming messages of a given type to drop for a client: "clientId|type" -> remaining.
var drops = new ConcurrentDictionary<string, int>();
app.Services.GetRequiredService<Gateway>().OutboundFilter = (clientId, payload) =>
{
    var text = Encoding.UTF8.GetString(payload);
    foreach (var key in drops.Keys)
    {
        var separator = key.IndexOf('|');
        if (key[..separator] != clientId || !text.Contains($"\"t\":\"{key[(separator + 1)..]}\"", StringComparison.Ordinal)) continue;
        while (drops.TryGetValue(key, out var remaining) && remaining > 0)
        {
            if (drops.TryUpdate(key, remaining - 1, remaining)) return false;
        }
    }

    return true;
};

app.MapGet("/health", () => "ok");

// Drop the next {count} messages of envelope type {type} (for example "changed" or "$reply") sent to {clientId}.
app.MapPost("/control/drop", (string clientId, string type, int count) =>
{
    drops[$"{clientId}|{type}"] = count;
    return Results.Ok();
});

// Cut a client's connection from the server side, like a network failure.
app.MapPost("/control/disconnect", async (string clientId, MqttServer server) =>
{
    await server.DisconnectClientAsync(clientId, new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });
    return Results.Ok();
});

// Make saving fail for a persistent room (by room id).
app.MapPost("/control/fail-writes", (string roomId, bool on) =>
{
    if (on) SharedTestStorage.FailWrites[roomId] = true;
    else SharedTestStorage.FailWrites.TryRemove(roomId, out _);
    return Results.Ok();
});

await app.RunAsync();

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var free = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return free;
}
