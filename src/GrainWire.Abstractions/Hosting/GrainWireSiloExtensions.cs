using System.Text.Json;
using GrainWire.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;

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

/// <summary>The default payload serializer: System.Text.Json with web defaults (camelCase).</summary>
public sealed class JsonRoomSerializer : IRoomSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonRoomSerializer(JsonSerializerOptions? options = null) => _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public byte[]? Serialize(object? value) => value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), _options);

    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, _options);
}

/// <summary>Refuses to start a silo that contains a room grain which could run two operations at once.</summary>
internal sealed class RoomGrainValidator(IOptions<GrainTypeOptions> grainTypes) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(nameof(RoomGrainValidator), ServiceLifecycleStage.RuntimeInitialize, cancellationToken =>
        {
            foreach (Type grainClass in grainTypes.Value.Classes)
            {
                if (!IsRoomGrain(grainClass) || grainClass.IsAbstract)
                {
                    continue;
                }

                _ = RoomTypeInfo.For(grainClass);
            }

            return Task.CompletedTask;
        });
    }

    private static bool IsRoomGrain(Type type)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(RoomGrain<>))
            {
                return true;
            }
        }

        return false;
    }
}
