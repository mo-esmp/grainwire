using System.Text.Json;

namespace GrainWire;

/// <summary>
/// Converts app payloads to and from the bytes carried in the <c>d</c> field. v1 envelopes are JSON, so the output must be valid JSON.
/// </summary>
public interface IRoomSerializer
{
    /// <summary>
    /// Converts a payload to bytes.
    /// </summary>
    /// <param name="value">The payload, or null for no payload.</param>
    /// <returns>The serialized bytes, or null when <paramref name="value"/> is null.</returns>
    byte[]? Serialize(object? value);

    /// <summary>
    /// Converts bytes back to a payload.
    /// </summary>
    /// <param name="data">The serialized bytes.</param>
    /// <param name="type">The type to produce.</param>
    /// <returns>The payload, or null.</returns>
    object? Deserialize(byte[] data, Type type);
}

/// <summary>
/// The default payload serializer: System.Text.Json with web defaults (camelCase).
/// </summary>
public sealed class JsonRoomSerializer : IRoomSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a serializer.
    /// </summary>
    /// <param name="options">Custom options. Null uses <see cref="JsonSerializerDefaults.Web"/>.</param>
    public JsonRoomSerializer(JsonSerializerOptions? options = null) => _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public byte[]? Serialize(object? value) => value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), _options);

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, _options);
}
