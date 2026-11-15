using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Orleans.Runtime;

namespace GrainWire;

/// <summary>Who a person is. Comes only from the authenticated connection, never from a message payload.</summary>
[GenerateSerializer, Immutable]
[JsonConverter(typeof(PlayerIdJsonConverter))]
public readonly record struct PlayerId([property: Id(0)] string Value)
{
    public override string ToString() => Value;
}

/// <summary>One browser tab's connection: the MQTT client id plus a generation that changes on every reconnect.</summary>
[GenerateSerializer, Immutable]
public readonly record struct ConnectionId([property: Id(0)] string ClientId, [property: Id(1)] long Generation)
{
    public override string ToString() => $"{ClientId}@{Generation}";
}

/// <summary>Identifies one gateway process for its lifetime.</summary>
[GenerateSerializer, Immutable]
public readonly record struct GatewayId([property: Id(0)] string Value)
{
    public override string ToString() => Value;
}

/// <summary>Presence of a member in a room.</summary>
[GenerateSerializer]
public enum PresenceStatus
{
    /// <summary>At least one connection is attached.</summary>
    Online = 0,
    /// <summary>No connection is attached; the grace period is running.</summary>
    Reconnecting = 1,
    /// <summary>The member is gone (grace period expired, left, or was removed).</summary>
    Left = 2,
}

/// <summary>Address of a room: its type (which grain class) and its id.</summary>
[GenerateSerializer, Immutable]
public readonly partial record struct RoomAddress([property: Id(0)] string Type, [property: Id(1)] string Id)
{
    public override string ToString() => $"{Type}/{Id}";

    /// <summary>True when <paramref name="segment"/> can be used as a room type, room id or command name.</summary>
    public static bool IsValidSegment(string? segment) => segment is { Length: > 0 and <= 128 } && SegmentRegex().IsMatch(segment);

    /// <summary>The Orleans grain type that hosts rooms of <paramref name="roomType"/>.</summary>
    public static GrainType GrainTypeFor(string roomType) => GrainType.Create("gw-room-" + roomType);

    /// <summary>The grain id of this room.</summary>
    public GrainId ToGrainId() => GrainId.Create(GrainTypeFor(Type), Id);

    [GeneratedRegex("^[A-Za-z0-9_.~-]+$")]
    private static partial Regex SegmentRegex();
}

internal sealed class PlayerIdJsonConverter : JsonConverter<PlayerId>
{
    public override PlayerId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? "");
    public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
