namespace GrainWire;

/// <summary>One browser tab's connection: the MQTT client id plus a generation that changes on every reconnect.</summary>
[GenerateSerializer, Immutable]
public readonly record struct ConnectionId([property: Id(0)] string ClientId, [property: Id(1)] long Generation)
{
    public override string ToString() => $"{ClientId}@{Generation}";
}
