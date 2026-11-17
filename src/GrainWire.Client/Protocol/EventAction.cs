namespace GrainWire.Client.Protocol;

/// <summary>Apply this event: it is the next one of the stream.</summary>
public sealed record EventAction(EventEnvelope Event) : OrderingAction;
