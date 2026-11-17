namespace GrainWire.Client;

/// <summary>The request was not sent because there is no connection right now.</summary>
public sealed class NotConnectedException() : GrainWireClientException("Not connected.");
