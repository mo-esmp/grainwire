namespace GrainWire;

/// <summary>
/// App-defined data sent by a client with a join request or a command.
/// </summary>
public readonly struct RoomPayload
{
    private readonly byte[]? _bytes;
    private readonly IRoomSerializer? _serializer;

    internal RoomPayload(byte[]? bytes, IRoomSerializer serializer)
    {
        _bytes = bytes;
        _serializer = serializer;
    }

    /// <summary>
    /// True when the client sent no data.
    /// </summary>
    public bool IsEmpty => _bytes is null || _bytes.Length == 0;

    /// <summary>
    /// Reads the payload as <typeparamref name="T"/>, or returns default when it is empty.
    /// </summary>
    /// <typeparam name="T">The type to read the payload as.</typeparam>
    /// <returns>The payload, or default when it is empty.</returns>
    public T? As<T>() => IsEmpty ? default : (T?)_serializer!.Deserialize(_bytes!, typeof(T));
}
