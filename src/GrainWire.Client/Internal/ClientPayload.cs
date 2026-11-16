using System.Buffers;
using System.Text.Json;

namespace GrainWire.Client.Internal;

/// <summary>
/// Writes the JSON objects a client publishes (docs/protocol.md, section 3). Fields that have no value are left out.
/// Parameters named <c>...Json</c> are JSON text that was serialized earlier, and are copied as they are.
/// </summary>
internal static class ClientPayload
{
    public static byte[] Join(string commandId, bool create, byte[]? optionsJson, byte[]? dataJson) => Write(writer =>
    {
        writer.WriteString("cmd"u8, commandId);
        writer.WriteBoolean("create"u8, create);
        Raw(writer, "options"u8, optionsJson);
        Raw(writer, "data"u8, dataJson);
    });

    public static byte[] Command(string commandId, string? incarnation, byte[]? dataJson, bool noReply) => Write(writer =>
    {
        writer.WriteString("cmd"u8, commandId);
        if (noReply)
        {
            writer.WriteBoolean("noreply"u8, true);
        }

        Text(writer, "ri"u8, incarnation);
        Raw(writer, "d"u8, dataJson);
    });

    public static byte[] Sync(string commandId, string? incarnation, string? epoch, long seq) => Write(writer =>
    {
        writer.WriteString("cmd"u8, commandId);
        Text(writer, "ri"u8, incarnation);
        Text(writer, "se"u8, epoch);
        writer.WriteNumber("seq"u8, seq);
    });

    public static byte[] Leave(string commandId) => Write(writer => writer.WriteString("cmd"u8, commandId));

    private static byte[] Write(Action<Utf8JsonWriter> fields)
    {
        ArrayBufferWriter<byte> buffer = new(128);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            fields(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void Text(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void Raw(Utf8JsonWriter writer, ReadOnlySpan<byte> name, byte[]? json)
    {
        if (json is not null)
        {
            writer.WritePropertyName(name);
            writer.WriteRawValue(json, skipInputValidation: true);
        }
    }
}
