using System.Text.Json;

namespace GrainWire.Client.Internal;

/// <summary>Converts app data to and from the JSON carried in <c>d</c>, <c>options</c> and <c>data</c>.</summary>
internal static class Payloads
{
    /// <summary>Returns null for a value that should be left out of the message.</summary>
    public static byte[]? Serialize(object? value, JsonSerializerOptions options)
    {
        if (value is null || value is JsonElement { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null })
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
    }

    /// <summary>Returns the default of <typeparamref name="T"/> when the message carried no value.</summary>
    public static T? Deserialize<T>(JsonElement element, JsonSerializerOptions options) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : element.Deserialize<T>(options);
}
