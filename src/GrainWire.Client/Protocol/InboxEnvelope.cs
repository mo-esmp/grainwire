using System.Diagnostics.CodeAnalysis;
using GrainWire.Client.Internal;

namespace GrainWire.Client.Protocol;

/// <summary>A message the gateway delivers to a client's inbox. See docs/protocol.md, section 4.</summary>
public abstract record InboxEnvelope
{
    private protected InboxEnvelope()
    {
    }

    /// <summary>The envelope type (<c>t</c>). Types starting with <c>$</c> belong to the framework; every other type is an app event.</summary>
    public required string Type { get; init; }

    /// <summary>The room the message is about, as <c>type/id</c>.</summary>
    public required string Room { get; init; }

    /// <summary>
    /// Reads an inbox message. Returns false when the payload is not a message this client understands: it is not a
    /// JSON object, or a field its type requires is missing.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out InboxEnvelope? envelope) =>
        InboxParser.TryParse(utf8Json, out envelope);
}
