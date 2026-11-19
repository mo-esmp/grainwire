using System.Text.RegularExpressions;

namespace GrainWire;

/// <summary>
/// Address of a room: its type (which grain class) and its id.
/// </summary>
/// <param name="Type">The room type name.</param>
/// <param name="Id">The room id.</param>
[GenerateSerializer, Immutable]
public readonly partial record struct RoomAddress([property: Id(0)] string Type, [property: Id(1)] string Id)
{
    /// <inheritdoc />
    public override string ToString() => $"{Type}/{Id}";

    /// <summary>
    /// True when <paramref name="segment"/> can be used as a room type, room id or command name.
    /// </summary>
    /// <param name="segment">The text to check.</param>
    /// <returns>True when the segment is valid.</returns>
    public static bool IsValidSegment(string? segment) => segment is { Length: > 0 and <= 128 } && SegmentRegex().IsMatch(segment);

    /// <summary>
    /// The Orleans grain type that hosts rooms of <paramref name="roomType"/>.
    /// </summary>
    /// <param name="roomType">The room type name.</param>
    /// <returns>The grain type.</returns>
    public static GrainType GrainTypeFor(string roomType) => GrainType.Create("gw-room-" + roomType);

    /// <summary>
    /// The grain id of this room.
    /// </summary>
    /// <returns>The grain id.</returns>
    public GrainId ToGrainId() => GrainId.Create(GrainTypeFor(Type), Id);

    [GeneratedRegex("^[A-Za-z0-9_.~-]+$")]
    private static partial Regex SegmentRegex();
}
