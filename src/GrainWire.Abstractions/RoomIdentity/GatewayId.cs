namespace GrainWire;

/// <summary>Identifies one gateway process for its lifetime.</summary>
[GenerateSerializer, Immutable]
public readonly record struct GatewayId([property: Id(0)] string Value)
{
    public override string ToString() => Value;
}
