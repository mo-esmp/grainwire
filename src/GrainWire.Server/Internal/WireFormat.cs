using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using GrainWire.Protocol;

namespace GrainWire.Server.Internal;

internal enum TopicKind
{
    Command,
    System,
}

/// <summary>A parsed client-to-server topic: <c>rooms/{type}/{id}/cmd|sys/{name}</c>.</summary>
internal readonly record struct ClientTopic(RoomAddress Room, TopicKind Kind, string Name)
{
    public static bool TryParse(string? topic, out ClientTopic result)
    {
        result = default;
        if (topic is null)
        {
            return false;
        }

        string[] parts = topic.Split('/');
        if (parts.Length != 5 || parts[0] != "rooms")
        {
            return false;
        }

        TopicKind kind;
        if (parts[3] == "cmd")
        {
            kind = TopicKind.Command;
        }
        else if (parts[3] == "sys")
        {
            kind = TopicKind.System;
        }
        else
        {
            return false;
        }

        if (!RoomAddress.IsValidSegment(parts[1]) || !RoomAddress.IsValidSegment(parts[2]) || !RoomAddress.IsValidSegment(parts[4]))
        {
            return false;
        }

        result = new ClientTopic(new RoomAddress(parts[1], parts[2]), kind, parts[4]);
        return true;
    }
}

/// <summary>The JSON object a client publishes. Every field is optional.</summary>
internal sealed class ClientMessage
{
    public string? CommandId;
    public string? Incarnation;
    public bool NoReply;
    public byte[]? Data;
    public bool Create;
    public byte[]? Options;
    public byte[]? JoinData;
    public string? Epoch;
    public long Seq;

    public static bool TryParse(ReadOnlyMemory<byte> payload, out ClientMessage message)
    {
        message = new ClientMessage();
        if (payload.Length == 0)
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                JsonElement value = property.Value;
                switch (property.Name)
                {
                    case "cmd": message.CommandId = AsString(value, 128); break;
                    case "ri": message.Incarnation = AsString(value, 64); break;
                    case "se": message.Epoch = AsString(value, 64); break;
                    case "noreply": message.NoReply = value.ValueKind == JsonValueKind.True; break;
                    case "create": message.Create = value.ValueKind == JsonValueKind.True; break;
                    case "seq": message.Seq = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seq) ? seq : -1; break;
                    case "d": message.Data = Raw(value); break;
                    case "options": message.Options = Raw(value); break;
                    case "data": message.JoinData = Raw(value); break;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? AsString(JsonElement value, int maxLength) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maxLength ? text : null;

    private static byte[]? Raw(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : Encoding.UTF8.GetBytes(value.GetRawText());
}

/// <summary>Builds the JSON envelopes the gateway sends to a client's inbox. See docs/protocol.md.</summary>
internal static class Envelopes
{
    public static byte[] Reply(string room, string? commandId, RoomReply reply)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", Wire.Types.Reply);
            writer.WriteString("room", room);
            if (commandId is not null)
            {
                writer.WriteString("cmd", commandId);
            }

            writer.WriteString("status", reply.Status switch
            {
                ReplyStatus.Ok => Wire.Status.Ok,
                ReplyStatus.Rejected => Wire.Status.Rejected,
                _ => Wire.Status.Error,
            });
            if (reply.Code is not null)
            {
                writer.WriteString("code", reply.Code);
            }

            if (reply.Reason is not null)
            {
                writer.WriteString("reason", reply.Reason);
            }

            if (reply.Incarnation is not null)
            {
                writer.WriteString("ri", reply.Incarnation);
            }

            if (reply.Data is { Length: > 0 })
            {
                writer.WritePropertyName("d");
                writer.WriteRawValue(reply.Data, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Snapshot(string room, SnapshotData snapshot)
    {
        ArrayBufferWriter<byte> buffer = new(512 + (snapshot.View?.Length ?? 0));
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", Wire.Types.Snapshot);
            writer.WriteString("room", room);
            writer.WriteString("ri", snapshot.Incarnation);
            writer.WriteString("se", snapshot.Epoch);
            writer.WriteNumber("at", snapshot.At);
            writer.WritePropertyName("d");
            if (snapshot.View is { Length: > 0 })
            {
                writer.WriteRawValue(snapshot.View, skipInputValidation: true);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteStartArray("members");
            foreach (MemberInfo member in snapshot.Members)
            {
                WriteMember(writer, member);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("you");
            WriteMember(writer, snapshot.You);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Closed(string room, string? incarnation, string reason, string? message)
    {
        ArrayBufferWriter<byte> buffer = new(128);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", Wire.Types.Closed);
            writer.WriteString("room", room);
            if (!string.IsNullOrEmpty(incarnation))
            {
                writer.WriteString("ri", incarnation);
            }

            writer.WriteStartObject("d");
            writer.WriteString("reason", reason);
            if (message is not null)
            {
                writer.WriteString("message", message);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>An unsequenced notice whose payload the room already serialized (<c>$closed</c>).</summary>
    public static byte[] Notice(DeliveryBatch batch, DeliveryItem item)
    {
        ArrayBufferWriter<byte> buffer = new(128 + (item.Data?.Length ?? 0));
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", item.Type);
            writer.WriteString("room", batch.Room);
            if (!string.IsNullOrEmpty(batch.Incarnation))
            {
                writer.WriteString("ri", batch.Incarnation);
            }

            if (item.Data is { Length: > 0 })
            {
                writer.WritePropertyName("d");
                writer.WriteRawValue(item.Data, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMember(Utf8JsonWriter writer, MemberInfo member)
    {
        writer.WriteStartObject();
        writer.WriteString("player", member.Player);
        if (member.Role is not null)
        {
            writer.WriteString("role", member.Role);
        }

        writer.WriteString("status", member.Status switch
        {
            PresenceStatus.Online => "online",
            PresenceStatus.Reconnecting => "reconnecting",
            _ => "left",
        });
        writer.WriteEndObject();
    }
}

/// <summary>
/// One sequenced event, pre-rendered so that sending it to many connections only splices in each player's <c>seq</c>.
/// </summary>
internal readonly struct EventTemplate
{
    private readonly byte[] _head;
    private readonly byte[] _tail;

    public EventTemplate(DeliveryBatch batch, DeliveryItem item)
    {
        ArrayBufferWriter<byte> buffer = new(160);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", item.Type);
            writer.WriteString("room", batch.Room);
            writer.WriteString("ri", batch.Incarnation);
            writer.WriteString("se", batch.Epoch);
            writer.WritePropertyName("seq");
            writer.Flush();
        }

        _head = buffer.WrittenSpan.ToArray();
        if (item.Data is { Length: > 0 } data)
        {
            _tail = new byte[data.Length + 6];
            ",\"d\":"u8.CopyTo(_tail);
            data.CopyTo(_tail.AsSpan(5));
            _tail[^1] = (byte)'}';
        }
        else
        {
            _tail = "}"u8.ToArray();
        }
    }

    public byte[] Render(long seq)
    {
        Span<byte> digits = stackalloc byte[20];
        Utf8Formatter.TryFormat(seq, digits, out int written);
        byte[] result = new byte[_head.Length + written + _tail.Length];
        _head.CopyTo(result, 0);
        digits[..written].CopyTo(result.AsSpan(_head.Length));
        _tail.CopyTo(result, _head.Length + written);
        return result;
    }
}
