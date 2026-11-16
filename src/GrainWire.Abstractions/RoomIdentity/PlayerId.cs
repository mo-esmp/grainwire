using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrainWire;

/// <summary>Who a person is. Comes only from the authenticated connection, never from a message payload.</summary>
[GenerateSerializer, Immutable]
[JsonConverter(typeof(PlayerIdJsonConverter))]
public readonly record struct PlayerId([property: Id(0)] string Value)
{
    public override string ToString() => Value;
}

internal sealed class PlayerIdJsonConverter : JsonConverter<PlayerId>
{
    public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? "");
    public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

