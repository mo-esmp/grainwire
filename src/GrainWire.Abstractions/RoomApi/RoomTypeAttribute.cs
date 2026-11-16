using Orleans.Metadata;

namespace GrainWire;

/// <summary>Marks a <see cref="RoomGrain{TState}"/> subclass and gives the room type its name (the first topic segment after <c>rooms/</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RoomTypeAttribute : Attribute, IGrainTypeProviderAttribute
{
    public RoomTypeAttribute(string name)
    {
        if (!RoomAddress.IsValidSegment(name))
        {
            throw new ArgumentException($"'{name}' is not a valid room type name.", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    GrainType IGrainTypeProviderAttribute.GetGrainType(IServiceProvider services, Type type) => RoomAddress.GrainTypeFor(Name);
}
