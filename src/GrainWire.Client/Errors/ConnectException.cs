namespace GrainWire.Client;

/// <summary>The first connection could not be made: the server refused it, could not be reached, or did not answer in time.</summary>
public sealed class ConnectException(string message, int? reasonCode, Exception? innerException = null)
    : GrainWireClientException(message, innerException)
{
    /// <summary>The MQTT CONNACK reason code, when the server refused the connection.</summary>
    public int? ReasonCode { get; } = reasonCode;
}
