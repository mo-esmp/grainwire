using System.Collections.Concurrent;
using System.Text;
using GrainWire.Server.Internal;
using GrainWire.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace GrainWire.Tests.Client;

/// <summary>
/// A real GrainWire server for the .NET client's integration tests: one silo with its gateway, in this process, with
/// the same fault injection tests/GrainWire.TestHost offers the TypeScript client over HTTP.
/// </summary>
public sealed class ClientServerFixture : IAsyncLifetime
{
    // How many upcoming messages of a given type to drop for a client: "clientId|type" -> remaining.
    private readonly ConcurrentDictionary<string, int> _drops = new();

    public TestNode Node { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Node = await TestNode.StartSiloWithGateway(new TestClusterInfo(), primary: true, options =>
        {
            options.CommandsPerSecond = 10_000;
            options.CommandBurst = 10_000;
            options.RoomCreationsPerMinute = 1_000_000;
            options.MaxConnectionsPerPlayer = 1000;
        });
        Node.App.Services.GetRequiredService<Gateway>().OutboundFilter = Deliver;
    }

    public async Task DisposeAsync() => await Node.DisposeAsync();

    /// <summary>Drops the next <paramref name="count"/> messages of envelope type <paramref name="type"/> (for example "changed" or "$reply") sent to a client.</summary>
    public void Drop(string clientId, string type, int count) => _drops[$"{clientId}|{type}"] = count;

    /// <summary>Cuts a client's connection from the server side, like a network failure.</summary>
    public Task Disconnect(string clientId) =>
        Node.App.Services.GetRequiredService<MqttServer>().DisconnectClientAsync(clientId, new MqttServerClientDisconnectOptions { ReasonCode = MqttDisconnectReasonCode.UnspecifiedError });

    private bool Deliver(string clientId, byte[] payload)
    {
        if (_drops.IsEmpty)
        {
            return true;
        }

        string text = Encoding.UTF8.GetString(payload);
        foreach (string key in _drops.Keys)
        {
            int separator = key.IndexOf('|');
            if (key[..separator] != clientId || !text.Contains($"\"t\":\"{key[(separator + 1)..]}\"", StringComparison.Ordinal))
            {
                continue;
            }

            while (_drops.TryGetValue(key, out int remaining) && remaining > 0)
            {
                if (_drops.TryUpdate(key, remaining - 1, remaining))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
