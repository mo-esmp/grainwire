using System.Net;
using System.Net.Sockets;
using GrainWire.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;

namespace GrainWire.Tests.Support;

/// <summary>Ports and names of one test cluster, shared by its nodes.</summary>
public sealed class TestClusterInfo
{
    public string ClusterId { get; } = "gw-test-" + Guid.NewGuid().ToString("N")[..8];
    public int PrimarySiloPort { get; } = Ports.Next();
    public List<int> GatewayPorts { get; } = [];
}

public static class Ports
{
    public static int Next()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>One process-like node of a test cluster: a silo, a gateway, or both, hosted in a real ASP.NET Core app.</summary>
public sealed class TestNode : IAsyncDisposable
{
    private TestNode(WebApplication app, int httpPort, bool hasGateway, int siloPort)
    {
        App = app;
        HttpPort = httpPort;
        HasGateway = hasGateway;
        SiloPort = siloPort;
    }

    /// <summary>The silo-to-silo port, or 0 for a standalone gateway. Room grains report it in <c>RuntimeIdentity</c>.</summary>
    public int SiloPort { get; }

    /// <summary>True when <paramref name="runtimeIdentity"/> (from the "where" command) names this node's silo.</summary>
    public bool Hosts(string? runtimeIdentity) => SiloPort != 0 && runtimeIdentity is not null && runtimeIdentity.Contains($":{SiloPort}:", StringComparison.Ordinal);

    public WebApplication App { get; }
    public int HttpPort { get; }
    public bool HasGateway { get; }
    public string MqttUrl => $"ws://127.0.0.1:{HttpPort}/mqtt";
    public IGrainFactory Grains => App.Services.GetRequiredService<IGrainFactory>();

    /// <summary>Mode A: an Orleans silo with the gateway in the same process.</summary>
    public static Task<TestNode> StartSiloWithGateway(TestClusterInfo cluster, bool primary, Action<GrainWireGatewayOptions>? gateway = null) =>
        Start(cluster, silo: true, primary, withGateway: true, gateway);

    /// <summary>A silo with no MQTT endpoint.</summary>
    public static Task<TestNode> StartSiloOnly(TestClusterInfo cluster, bool primary) => Start(cluster, silo: true, primary, withGateway: false, null);

    /// <summary>Mode B: a standalone gateway that is only an Orleans client.</summary>
    public static Task<TestNode> StartGatewayOnly(TestClusterInfo cluster, Action<GrainWireGatewayOptions>? gateway = null) =>
        Start(cluster, silo: false, primary: false, withGateway: true, gateway);

    private static async Task<TestNode> Start(TestClusterInfo cluster, bool silo, bool primary, bool withGateway, Action<GrainWireGatewayOptions>? gateway)
    {
        int httpPort = Ports.Next();
        int siloPort = 0;
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");

        if (silo)
        {
            siloPort = primary ? cluster.PrimarySiloPort : Ports.Next();
            int thisSiloPort = siloPort;
            int gatewayPort = Ports.Next();
            cluster.GatewayPorts.Add(gatewayPort);
            builder.UseOrleans(orleans =>
            {
                orleans.UseLocalhostClustering(thisSiloPort, gatewayPort, primary ? null : new IPEndPoint(IPAddress.Loopback, cluster.PrimarySiloPort), cluster.ClusterId, cluster.ClusterId);
                orleans.Configure<ClusterMembershipOptions>(o =>
                {
                    // Notice a stopped silo quickly.
                    o.ProbeTimeout = TimeSpan.FromSeconds(1);
                    o.NumMissedProbesLimit = 1;
                    o.NumVotesForDeathDeclaration = 1;
                    o.TableRefreshTimeout = TimeSpan.FromSeconds(1);
                    o.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(1);
                });
                TestSilo.Configure(orleans);
            });
        }
        else
        {
            builder.UseOrleansClient(client =>
            {
                client.UseLocalhostClustering([.. cluster.GatewayPorts], cluster.ClusterId, cluster.ClusterId);
                client.AddGrainWireClient();
            });
        }

        if (withGateway)
        {
            builder.Services.AddGrainWireGateway(options =>
            {
                options.LeaseRenewInterval = TimeSpan.FromMilliseconds(300);
                gateway?.Invoke(options);
            });
        }

        WebApplication app = builder.Build();
        if (withGateway)
        {
            app.MapGrainWire();
        }

        await app.StartAsync();
        return new TestNode(app, httpPort, withGateway, siloPort);
    }

    public async Task StopAsync()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        await App.StopAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch
        {
            // Already stopped, or stopping timed out: nothing more to do in a test.
        }

        await App.DisposeAsync();
    }
}

/// <summary>
/// The cluster most gateway tests share: two silos that each run a gateway (mode A), plus a standalone gateway
/// (mode B) with tight limits. Tests use their own room ids.
/// </summary>
public sealed class GatewayClusterFixture : IAsyncLifetime
{
    public TestClusterInfo Info { get; } = new();
    public TestNode NodeA { get; private set; } = null!;
    public TestNode NodeB { get; private set; } = null!;
    public TestNode Strict { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        NodeA = await TestNode.StartSiloWithGateway(Info, primary: true);
        NodeB = await TestNode.StartSiloWithGateway(Info, primary: false);
        Strict = await TestNode.StartGatewayOnly(Info, options =>
        {
            options.CommandBurst = 5;
            options.CommandsPerSecond = 0.5;
            options.MaxRoomsPerConnection = 2;
            options.MaxConnectionsPerPlayer = 2;
            options.MaxPayloadBytes = 512;
            options.RoomCreationsPerMinute = 3;
        });
    }

    public async Task DisposeAsync()
    {
        await Strict.DisposeAsync();
        await NodeB.DisposeAsync();
        await NodeA.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<GatewayClusterFixture>
{
    public const string Name = "gateway";
}
