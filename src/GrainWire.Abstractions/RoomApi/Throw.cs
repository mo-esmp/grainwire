using System.Diagnostics.CodeAnalysis;

namespace GrainWire;

internal static class Throw
{
    [DoesNotReturn]
    public static void Rejected(string reason, string? code) => throw new RoomRejectedException(reason, code);
}
