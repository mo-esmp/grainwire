using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using GrainWire.Protocol;

namespace GrainWire.Server.Internal;

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
