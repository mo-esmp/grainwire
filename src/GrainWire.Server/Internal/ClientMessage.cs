using System.Text;
using System.Text.Json;

namespace GrainWire.Server.Internal;

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
