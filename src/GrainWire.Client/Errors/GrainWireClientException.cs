namespace GrainWire.Client;

/// <summary>Base class of the errors the GrainWire client reports.</summary>
public abstract class GrainWireClientException : Exception
{
    private protected GrainWireClientException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
