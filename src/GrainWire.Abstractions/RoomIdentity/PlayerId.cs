using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrainWire;

/// <summary>
/// Who a person is. Comes only from the authenticated connection, never from a message payload.
/// </summary>
/// <param name="Value">The player id text.</param>
[GenerateSerializer, Immutable]
[JsonConverter(typeof(PlayerIdJsonConverter))]
public readonly record struct PlayerId([property: Id(0)] string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Reads and writes a <see cref="PlayerId"/> as a plain JSON string.
/// </summary>
internal sealed class PlayerIdJsonConverter : JsonConverter<PlayerId>
{
    /// <inheritdoc />
    public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? "");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
