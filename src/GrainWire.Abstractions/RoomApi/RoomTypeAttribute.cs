using Orleans.Metadata;

namespace GrainWire;

/// <summary>
/// Marks a <see cref="RoomGrain{TState}"/> subclass and gives the room type its name (the first topic segment after <c>rooms/</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RoomTypeAttribute : Attribute, IGrainTypeProviderAttribute
{
    /// <summary>
    /// Names the room type.
    /// </summary>
    /// <param name="name">The room type name. It must be a valid address segment.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid room type name.</exception>
    public RoomTypeAttribute(string name)
    {
        if (!RoomAddress.IsValidSegment(name))
        {
            throw new ArgumentException($"'{name}' is not a valid room type name.", nameof(name));
        }

        Name = name;
    }

    /// <summary>
    /// The room type name.
    /// </summary>
    public string Name { get; }

    /// <inheritdoc />
    GrainType IGrainTypeProviderAttribute.GetGrainType(IServiceProvider services, Type type) => RoomAddress.GrainTypeFor(Name);
}
