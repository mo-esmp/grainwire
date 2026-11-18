using System.Buffers;
using System.Text.Json;
using GrainWire.Protocol;

namespace GrainWire.Server.Internal;

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

            // Which snapshot went out just before this reply. The snapshot can be lost on the way while the reply
            // arrives, so the client must be able to tell it from another snapshot that turns up in between.
            if (reply.Snapshot is not null)
            {
                writer.WriteString("se", reply.Snapshot.Epoch);
                writer.WriteNumber("at", reply.Snapshot.At);
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
