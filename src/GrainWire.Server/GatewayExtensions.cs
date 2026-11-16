using GrainWire.Server.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MQTTnet.AspNetCore;
using MQTTnet.Server;

namespace GrainWire.Server;

public static class GrainWireGatewayExtensions
{
    /// <summary>
    /// Adds the GrainWire gateway: an MQTT broker (MQTTnet) over WebSockets that connects browsers to room grains.
    /// Works in a host that runs an Orleans silo (<c>UseOrleans</c>) and in one that is only an Orleans client
    /// (<c>UseOrleansClient</c>). Call <see cref="MapGrainWire"/> on the built app.
    /// </summary>
    public static IServiceCollection AddGrainWireGateway(this IServiceCollection services, Action<GrainWireGatewayOptions>? configure = null)
    {
        GrainWireGatewayOptions options = new();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddHostedMqttServer(mqtt => mqtt
            .WithoutDefaultEndpoint()
            .WithPersistentSessions(false)
            .WithMaxPendingMessagesPerClient(Math.Max(1, options.MaxOutgoingQueue))
            .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropNewMessage));
        services.AddMqttConnectionHandler();
        services.AddConnections();

        services.TryAddSingleton<IConnectionAuthenticator, DevelopmentAuthenticator>();
        services.AddSingleton<Gateway>();
        services.AddHostedService<LeaseRenewalService>();
        return services;
    }

    /// <summary>Maps the MQTT-over-WebSockets endpoint (default <c>/mqtt</c>) and starts routing.</summary>
    public static WebApplication MapGrainWire(this WebApplication app)
    {
        GrainWireGatewayOptions options = app.Services.GetRequiredService<GrainWireGatewayOptions>();
        Gateway gateway = app.Services.GetRequiredService<Gateway>();
        app.MapMqtt(options.Path);
        app.UseMqttServer(gateway.Attach);
        return app;
    }

    /// <summary>For a host that is only an Orleans client (a standalone gateway). Nothing extra is needed today; call it so future versions can add to it.</summary>
    public static IClientBuilder AddGrainWireClient(this IClientBuilder builder) => builder;
}
