using System.Text.RegularExpressions;

namespace GrainWire;

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
