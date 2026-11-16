using System.Text.Json;

namespace GrainWire;

/// <summary>Converts app payloads to and from the bytes carried in the <c>d</c> field. v1 envelopes are JSON, so the output must be valid JSON.</summary>
public interface IRoomSerializer
{
    byte[]? Serialize(object? value);
    object? Deserialize(byte[] data, Type type);
}

/// <summary>The default payload serializer: System.Text.Json with web defaults (camelCase).</summary>
public sealed class JsonRoomSerializer : IRoomSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonRoomSerializer(JsonSerializerOptions? options = null) => _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public byte[]? Serialize(object? value) => value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), _options);

    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, _options);
}
