using System.Text.Json;

namespace GrainWire.Internal;

/// <summary>
/// JSON for payloads the framework itself defines. Fixed, so the wire format does not depend on the app's serializer.
/// </summary>
internal static class FrameworkJson
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Serializes a framework payload to UTF-8 JSON with the fixed framework options.
    /// </summary>
    /// <typeparam name="T">The type of the payload.</typeparam>
    /// <param name="value">The payload to serialize.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, s_options);
}

/// <summary>
/// Payload of a <c>$presence</c> event: a member's presence changed.
/// </summary>
/// <param name="Player">The player whose presence changed.</param>
/// <param name="Status">The new presence status.</param>
/// <param name="Role">The member's role, if it has one.</param>
internal sealed record PresenceNotice(string Player, string Status, string? Role);

/// <summary>
/// Payload of a <c>$closed</c> notice: the connection's membership in the room ended.
/// </summary>
/// <param name="Reason">One of the values in <see cref="GrainWire.Protocol.Wire.ClosedReasons"/>.</param>
/// <param name="Message">Optional human-readable detail.</param>
internal sealed record ClosedNotice(string Reason, string? Message);
