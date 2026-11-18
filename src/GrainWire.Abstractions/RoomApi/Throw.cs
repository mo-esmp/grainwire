using System.Diagnostics.CodeAnalysis;

namespace GrainWire;

/// <summary>
/// Helpers that end a handler by throwing.
/// </summary>
internal static class Throw
{
    /// <summary>
    /// Throws a <see cref="RoomRejectedException"/>.
    /// </summary>
    /// <param name="reason">Why the command was rejected.</param>
    /// <param name="code">Optional machine-readable code for the client.</param>
    [DoesNotReturn]
    public static void Rejected(string reason, string? code) => throw new RoomRejectedException(reason, code);
}
