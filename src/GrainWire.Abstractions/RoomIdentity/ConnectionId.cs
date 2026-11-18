namespace GrainWire;

/// <summary>
/// One browser tab's connection: the MQTT client id plus a generation that changes on every reconnect.
/// </summary>
/// <param name="ClientId">The MQTT client id.</param>
/// <param name="Generation">Counts up on every reconnect of the same client id.</param>
[GenerateSerializer, Immutable]
public readonly record struct ConnectionId([property: Id(0)] string ClientId, [property: Id(1)] long Generation)
{
    /// <inheritdoc />
    public override string ToString() => $"{ClientId}@{Generation}";
}
