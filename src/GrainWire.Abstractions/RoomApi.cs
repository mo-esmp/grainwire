using System.Diagnostics.CodeAnalysis;
using Orleans.Metadata;
using Orleans.Runtime;

namespace GrainWire;

/// <summary>Marks a <see cref="RoomGrain{TState}"/> subclass and gives the room type its name (the first topic segment after <c>rooms/</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RoomTypeAttribute : Attribute, IGrainTypeProviderAttribute
{
    public RoomTypeAttribute(string name)
    {
        if (!RoomAddress.IsValidSegment(name))
        {
            throw new ArgumentException($"'{name}' is not a valid room type name.", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    GrainType IGrainTypeProviderAttribute.GetGrainType(IServiceProvider services, Type type) => RoomAddress.GrainTypeFor(Name);
}

/// <summary>
/// Marks a command handler. The method takes a <see cref="PlayerId"/> and optionally one payload parameter,
/// and returns nothing, a value, or a <see cref="Task"/> / <see cref="Task{TResult}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class OnMessageAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>Comma-separated roles allowed to send this command. Empty means every member.</summary>
    public string? Roles { get; set; }
}

/// <summary>How a room undoes state changes made by a handler that is rejected or throws.</summary>
public enum RollbackMode
{
    /// <summary>Copy the state before every handler and restore the copy on failure. Safe default.</summary>
    Copy = 0,
    /// <summary>No copy. Use only when state is large and every handler validates before it changes anything.</summary>
    None = 1,
}

/// <summary>Settings of one room type. Configure with <c>silo.AddGrainWire(gw =&gt; gw.ConfigureRoom("chess", o =&gt; ...))</c>.</summary>
public sealed class RoomOptions
{
    /// <summary>How long a member with no connection stays "reconnecting" before they are removed.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxMembers { get; set; } = 1000;

    public RollbackMode Rollback { get; set; } = RollbackMode.Copy;

    /// <summary>Name of an Orleans grain storage provider. Null (default) keeps the room in memory only.</summary>
    public string? StorageProvider { get; set; }

    /// <summary>A gateway that has not renewed for this long is treated as dead.</summary>
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(25);

    public TimeSpan LeaseCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Command ids remembered per player for retry de-duplication.</summary>
    public int DedupCapacity { get; set; } = 256;

    /// <summary>Command ids younger than this are always remembered, even beyond <see cref="DedupCapacity"/>.</summary>
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>A member of a room as handlers see it.</summary>
public sealed record RoomMember(PlayerId Player, string? Role, PresenceStatus Presence)
{
    public bool IsOnline => Presence == PresenceStatus.Online;
}

/// <summary>Why a member left the room.</summary>
public enum LeaveReason
{
    /// <summary>The reconnect grace period ran out.</summary>
    GraceExpired = 0,
    /// <summary>A handler removed the member.</summary>
    Removed = 1,
}

/// <summary>App-defined data sent by a client with a join request or a command.</summary>
public readonly struct RoomPayload
{
    private readonly byte[]? _bytes;
    private readonly IRoomSerializer? _serializer;

    internal RoomPayload(byte[]? bytes, IRoomSerializer serializer)
    {
        _bytes = bytes;
        _serializer = serializer;
    }

    public bool IsEmpty => _bytes is null || _bytes.Length == 0;

    /// <summary>Reads the payload as <typeparamref name="T"/>, or returns default when it is empty.</summary>
    public T? As<T>() => IsEmpty ? default : (T?)_serializer!.Deserialize(_bytes!, typeof(T));
}

public sealed class CreateContext
{
    internal CreateContext(PlayerId creator, IReadOnlyDictionary<string, string> claims, RoomPayload options)
    {
        Creator = creator;
        Claims = claims;
        Options = options;
    }

    public PlayerId Creator { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
    /// <summary>Room settings chosen by the creator (for example a time control).</summary>
    public RoomPayload Options { get; }
}

public sealed class JoinContext
{
    internal JoinContext(PlayerId player, IReadOnlyDictionary<string, string> claims, RoomPayload data, bool isCreator)
    {
        Player = player;
        Claims = claims;
        Data = data;
        IsCreator = isCreator;
    }

    public PlayerId Player { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }
    /// <summary>App-defined join data (for example a requested role or an invite code).</summary>
    public RoomPayload Data { get; }
    public bool IsCreator { get; }
}

public sealed class CreateResult
{
    private CreateResult(bool allowed, string? reason)
    {
        Allowed = allowed;
        Reason = reason;
    }

    public bool Allowed { get; }
    public string? Reason { get; }

    public static CreateResult Allow() => new(true, null);
    public static CreateResult Deny(string reason) => new(false, reason);
}

public sealed class JoinResult
{
    private JoinResult(bool accepted, string? role, string? reason)
    {
        Accepted = accepted;
        Role = role;
        Reason = reason;
    }

    public bool Accepted { get; }
    public string? Role { get; }
    public string? Reason { get; }

    public static JoinResult Accept(string? role = null) => new(true, role, null);
    public static JoinResult Reject(string reason) => new(false, null, reason);
}

/// <summary>Thrown by <c>Reject</c> to end a command without changing anything.</summary>
public sealed class RoomRejectedException(string reason, string? code = null) : Exception(reason)
{
    public string? Code { get; } = code;
}

/// <summary>Thrown when a change could not be saved and was rolled back.</summary>
public sealed class RoomNotCommittedException() : Exception("The change could not be saved and was rolled back.");

/// <summary>Converts app payloads to and from the bytes carried in the <c>d</c> field. v1 envelopes are JSON, so the output must be valid JSON.</summary>
public interface IRoomSerializer
{
    byte[]? Serialize(object? value);
    object? Deserialize(byte[] data, Type type);
}

internal static class Throw
{
    [DoesNotReturn]
    public static void Rejected(string reason, string? code) => throw new RoomRejectedException(reason, code);
}
