using System.Collections.Concurrent;
using System.Reflection;
using GrainWire.Protocol;
using Orleans.Concurrency;

namespace GrainWire.Internal;

/// <summary>Reflection data about one room grain class, computed once.</summary>
internal sealed class RoomTypeInfo
{
    private static readonly ConcurrentDictionary<Type, RoomTypeInfo> s_cache = new();

    private RoomTypeInfo(Type grainClass)
    {
        RoomTypeAttribute attribute = grainClass.GetCustomAttribute<RoomTypeAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{grainClass.FullName} derives from RoomGrain<TState> but has no [RoomType(\"...\")] attribute.");
        RoomType = attribute.Name;

        EnsureNotInterleaving(grainClass);

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo method in grainClass.GetMethods(flags))
        {
            OnMessageAttribute? onMessage = method.GetCustomAttribute<OnMessageAttribute>(inherit: true);
            if (onMessage is null)
            {
                continue;
            }

            if (!RoomAddress.IsValidSegment(onMessage.Name))
            {
                throw new InvalidOperationException($"[OnMessage(\"{onMessage.Name}\")] on {grainClass.Name}.{method.Name} is not a valid command name.");
            }

            if (!Handlers.TryAdd(onMessage.Name, new CommandHandler(method, onMessage)))
            {
                throw new InvalidOperationException($"{grainClass.Name} has more than one handler for command '{onMessage.Name}'.");
            }
        }

        OverridesOnRejoin = IsOverridden(grainClass, "OnRejoin");
        OverridesOnPresenceChanged = IsOverridden(grainClass, "OnPresenceChanged");
        OverridesOnPlayerLeft = IsOverridden(grainClass, "OnPlayerLeft");
    }

    public string RoomType { get; }
    public Dictionary<string, CommandHandler> Handlers { get; } = [with(StringComparer.Ordinal)];
    public bool OverridesOnRejoin { get; }
    public bool OverridesOnPresenceChanged { get; }
    public bool OverridesOnPlayerLeft { get; }

    public static RoomTypeInfo For(Type grainClass) => s_cache.GetOrAdd(grainClass, static t => new RoomTypeInfo(t));

    /// <summary>Throws when <paramref name="grainClass"/> could run two room operations at once.</summary>
    public static void EnsureNotInterleaving(Type grainClass)
    {
        if (grainClass.IsDefined(typeof(ReentrantAttribute), inherit: true) || grainClass.IsDefined(typeof(MayInterleaveAttribute), inherit: true))
        {
            throw new InvalidOperationException($"{grainClass.FullName} is a room grain and must not be [Reentrant] or [MayInterleave]: rooms process one operation at a time.");
        }

        foreach (Type contract in grainClass.GetInterfaces())
        {
            foreach (MethodInfo method in contract.GetMethods())
            {
                if (method.IsDefined(typeof(AlwaysInterleaveAttribute), inherit: true) || method.IsDefined(typeof(ReadOnlyAttribute), inherit: true))
                {
                    throw new InvalidOperationException($"{contract.FullName}.{method.Name} is marked [AlwaysInterleave] or [ReadOnly], which {grainClass.FullName} (a room grain) does not allow: rooms process one operation at a time.");
                }
            }
        }
    }

    private static bool IsOverridden(Type grainClass, string name)
    {
        MethodInfo? method = grainClass.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Type? declaring = method?.DeclaringType;
        return declaring is not null && !(declaring.IsGenericType && declaring.GetGenericTypeDefinition() == typeof(RoomGrain<>));
    }
}

internal sealed class CommandHandler
{
    private readonly MethodInfo _method;
    private readonly bool _takesPlayer;
    private readonly Type? _payloadType;
    private readonly PropertyInfo? _taskResult;
    private readonly MethodInfo? _valueTaskAsTask;

    public CommandHandler(MethodInfo method, OnMessageAttribute attribute)
    {
        _method = method;
        ParameterInfo[] parameters = method.GetParameters();
        int index = 0;
        if (parameters.Length > 0 && parameters[0].ParameterType == typeof(PlayerId))
        {
            _takesPlayer = true;
            index = 1;
        }

        if (parameters.Length - index > 1)
        {
            throw new InvalidOperationException($"Handler {method.DeclaringType?.Name}.{method.Name} may take a PlayerId and at most one payload parameter.");
        }

        if (parameters.Length - index == 1)
        {
            _payloadType = parameters[index].ParameterType;
        }

        Type returns = method.ReturnType;
        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
        {
            _taskResult = returns.GetProperty(nameof(Task<int>.Result));
        }

        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            _valueTaskAsTask = returns.GetMethod(nameof(ValueTask<int>.AsTask));
        }

        if (!string.IsNullOrWhiteSpace(attribute.Roles))
        {
            Roles = new HashSet<string>(attribute.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
        }
    }

    public HashSet<string>? Roles { get; }

    public async Task<object?> Invoke(object target, PlayerId player, byte[]? data, IRoomSerializer serializer)
    {
        object?[] args;
        if (_payloadType is not null)
        {
            object? payload;
            try
            {
                payload = data is null or { Length: 0 } ? null : serializer.Deserialize(data, _payloadType);
            }
            catch (Exception ex)
            {
                throw new RoomRejectedException("The command payload could not be read: " + ex.Message, Wire.Codes.BadPayload);
            }

            if (payload is null && _payloadType.IsValueType && Nullable.GetUnderlyingType(_payloadType) is null)
            {
                throw new RoomRejectedException("This command needs a payload.", Wire.Codes.BadPayload);
            }

            args = _takesPlayer ? [player, payload] : [payload];
        }
        else
        {
            args = _takesPlayer ? [player] : [];
        }

        object? returned = _method.Invoke(target, BindingFlags.DoNotWrapExceptions, binder: null, args, culture: null);
        switch (returned)
        {
            case null:
                return null;
            case Task task:
                await task;
                return _taskResult?.GetValue(task);
            case ValueTask valueTask:
                await valueTask;
                return null;
            default:
                if (_valueTaskAsTask is not null)
                {
                    Task asTask = (Task)_valueTaskAsTask.Invoke(returned, null)!;
                    await asTask;
                    return asTask.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(asTask);
                }

                return returned;
        }
    }
}
