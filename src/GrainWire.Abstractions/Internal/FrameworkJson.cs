using System.Text.Json;

namespace GrainWire.Internal;

/// <summary>JSON for payloads the framework itself defines. Fixed, so the wire format does not depend on the app's serializer.</summary>
internal static class FrameworkJson
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, s_options);
}

internal sealed record PresenceNotice(string Player, string Status, string? Role);

internal sealed record ClosedNotice(string Reason, string? Message);
