using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GrainWire;

public static class GrainWireSiloExtensions
{
    /// <summary>Registers what room grains need in this silo.</summary>
    public static ISiloBuilder AddGrainWire(this ISiloBuilder builder, Action<GrainWireSiloBuilder>? configure = null)
    {
        builder.Services.AddOptions<RoomOptions>();
        builder.Services.TryAddSingleton<IRoomSerializer>(_ => new JsonRoomSerializer());
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, RoomGrainValidator>();
        configure?.Invoke(new GrainWireSiloBuilder(builder.Services));
        return builder;
    }
}

public sealed class GrainWireSiloBuilder
{
    internal GrainWireSiloBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Settings for one room type (the name given to <see cref="RoomTypeAttribute"/>).</summary>
    public GrainWireSiloBuilder ConfigureRoom(string roomType, Action<RoomOptions> configure)
    {
        Services.Configure(roomType, configure);
        return this;
    }

    /// <summary>Settings applied to every room type, before its own settings.</summary>
    public GrainWireSiloBuilder ConfigureAllRooms(Action<RoomOptions> configure)
    {
        Services.ConfigureAll(configure);
        return this;
    }

    /// <summary>Replaces the payload serializer. v1 envelopes are JSON, so it must produce valid JSON.</summary>
    public GrainWireSiloBuilder UseSerializer(IRoomSerializer serializer)
    {
        Services.Replace(ServiceDescriptor.Singleton(serializer));
        return this;
    }

    /// <summary>Uses the built-in JSON serializer with custom options.</summary>
    public GrainWireSiloBuilder UseJson(JsonSerializerOptions options) => UseSerializer(new JsonRoomSerializer(options));
}
