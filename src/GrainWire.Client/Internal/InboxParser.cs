using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using GrainWire.Client.Protocol;

namespace GrainWire.Client.Internal;

/// <summary>Reads the messages a gateway sends to an inbox.</summary>
internal static class InboxParser
{
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out InboxEnvelope? envelope)
    {
        envelope = null;
        JsonElement root;
        try
        {
            // The element owns its memory, so the parts kept in an envelope (d) stay valid.
            Utf8JsonReader reader = new(utf8Json);
            root = JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object || Text(root, "t") is not { } type || Text(root, "room") is not { } room)
        {
            return false;
        }

        envelope = type switch
        {
            Wire.Reply => Reply(root, type, room),
            Wire.Closed => Closed(root, type, room),
            Wire.Snapshot => Snapshot(root, type, room),
            _ => Event(root, type, room),
        };
        return envelope is not null;
    }

    /// <summary>Reads the <c>d</c> of a <c>$presence</c> event.</summary>
    public static PresenceChange? Presence(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && Text(data, "player") is { } player
            ? new PresenceChange(player, Wire.ParseMemberStatus(Text(data, "status")), Text(data, "role"))
            : null;

    private static ReplyEnvelope Reply(JsonElement root, string type, string room) => new()
    {
        Type = type,
        Room = room,
        CommandId = Text(root, "cmd"),
        Status = Text(root, "status") switch
        {
            "ok" => ReplyStatus.Ok,
            "rejected" => ReplyStatus.Rejected,
            _ => ReplyStatus.Error,
        },
        Code = Text(root, "code"),
        Reason = Text(root, "reason"),
        Incarnation = Text(root, "ri"),
        Data = Data(root),
    };

    private static ClosedEnvelope Closed(JsonElement root, string type, string room)
    {
        JsonElement data = Data(root);
        bool hasData = data.ValueKind == JsonValueKind.Object;
        return new ClosedEnvelope
        {
            Type = type,
            Room = room,
            Incarnation = Text(root, "ri"),
            Reason = Wire.ParseClosedReason(hasData ? Text(data, "reason") : null),
            Message = hasData ? Text(data, "message") : null,
        };
    }

    private static SnapshotEnvelope? Snapshot(JsonElement root, string type, string room)
    {
        if (Text(root, "ri") is not { } incarnation
            || Text(root, "se") is not { } epoch
            || Number(root, "at") is not { } at
            || !root.TryGetProperty("you", out JsonElement youElement)
            || Member(youElement) is not { } you)
        {
            return null;
        }

        List<Member> members = [];
        if (root.TryGetProperty("members", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (Member(item) is { } member)
                {
                    members.Add(member);
                }
            }
        }

        return new SnapshotEnvelope
        {
            Type = type,
            Room = room,
            Incarnation = incarnation,
            Epoch = epoch,
            At = at,
            Data = Data(root),
            Members = members,
            You = you,
        };
    }

    private static EventEnvelope? Event(JsonElement root, string type, string room)
    {
        if (Text(root, "ri") is not { } incarnation || Text(root, "se") is not { } epoch || Number(root, "seq") is not { } seq)
        {
            return null;
        }

        return new EventEnvelope
        {
            Type = type,
            Room = room,
            Incarnation = incarnation,
            Epoch = epoch,
            Seq = seq,
            Data = Data(root),
        };
    }

    private static Member? Member(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && Text(element, "player") is { } player
            ? new Member(player, Text(element, "role"), Wire.ParseMemberStatus(Text(element, "status")))
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

    private static JsonElement Data(JsonElement root) => root.TryGetProperty("d", out JsonElement value) ? value : default;
}
