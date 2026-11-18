using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GrainWire;

/// <summary>
/// Extension methods that add GrainWire to an Orleans silo.
/// </summary>
public static class GrainWireSiloExtensions
{
    /// <summary>
    /// Registers what room grains need in this silo.
    /// </summary>
    /// <param name="builder">The silo builder to register GrainWire on.</param>
    /// <param name="configure">Optional callback to configure room types and the payload serializer.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
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

/// <summary>
/// Fluent builder passed to the callback of <see cref="GrainWireSiloExtensions.AddGrainWire"/>.
/// </summary>
public sealed class GrainWireSiloBuilder
{
    internal GrainWireSiloBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// The service collection of the silo being configured.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Settings for one room type (the name given to <see cref="RoomTypeAttribute"/>).
    /// </summary>
    /// <param name="roomType">The room type name.</param>
    /// <param name="configure">Callback that changes the options of that room type.</param>
    /// <returns>This builder, for chaining.</returns>
    public GrainWireSiloBuilder ConfigureRoom(string roomType, Action<RoomOptions> configure)
    {
        Services.Configure(roomType, configure);
        return this;
    }

    /// <summary>
    /// Settings applied to every room type, before its own settings.
    /// </summary>
    /// <param name="configure">Callback that changes the options of every room type.</param>
    /// <returns>This builder, for chaining.</returns>
    public GrainWireSiloBuilder ConfigureAllRooms(Action<RoomOptions> configure)
    {
        Services.ConfigureAll(configure);
        return this;
    }

    /// <summary>
    /// Replaces the payload serializer. v1 envelopes are JSON, so it must produce valid JSON.
    /// </summary>
    /// <param name="serializer">The serializer to use for app payloads.</param>
    /// <returns>This builder, for chaining.</returns>
    public GrainWireSiloBuilder UseSerializer(IRoomSerializer serializer)
    {
        Services.Replace(ServiceDescriptor.Singleton(serializer));
        return this;
    }

    /// <summary>
    /// Uses the built-in JSON serializer with custom options.
    /// </summary>
    /// <param name="options">The System.Text.Json options for app payloads.</param>
    /// <returns>This builder, for chaining.</returns>
    public GrainWireSiloBuilder UseJson(JsonSerializerOptions options) => UseSerializer(new JsonRoomSerializer(options));
}
