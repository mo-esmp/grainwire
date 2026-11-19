namespace GrainWire;

/// <summary>
/// Identifies one gateway process for its lifetime.
/// </summary>
/// <param name="Value">The id text.</param>
[GenerateSerializer, Immutable]
public readonly record struct GatewayId([property: Id(0)] string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}
