using System.Reflection;
using GrainWire.Protocol;

namespace GrainWire.Internal;

/// <summary>
/// Calls one <see cref="OnMessageAttribute"/> method with the player and the deserialized payload.
/// </summary>
internal sealed class CommandHandler
{
    private readonly MethodInfo _method;
    private readonly bool _takesPlayer;
    private readonly Type? _payloadType;
    private readonly PropertyInfo? _taskResult;
    private readonly MethodInfo? _valueTaskAsTask;

    /// <summary>
    /// Inspects the handler method once so calls are cheap.
    /// </summary>
    /// <param name="method">The handler method.</param>
    /// <param name="attribute">The attribute that marks the method.</param>
    /// <exception cref="InvalidOperationException">The method has more than a player and one payload parameter.</exception>
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
            throw new InvalidOperationException(
                $"Handler {method.DeclaringType?.Name}.{method.Name} may take a PlayerId and at most one payload parameter.");
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
            Roles = new HashSet<string>(
                attribute.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Roles allowed to send the command. Null means every member.
    /// </summary>
    public HashSet<string>? Roles { get; }

    /// <summary>
    /// Runs the handler.
    /// </summary>
    /// <param name="target">The room grain.</param>
    /// <param name="player">The player who sent the command.</param>
    /// <param name="data">The serialized payload, if any.</param>
    /// <param name="serializer">Used to read the payload.</param>
    /// <returns>The handler's return value, or null.</returns>
    /// <exception cref="RoomRejectedException">The payload is missing or cannot be read.</exception>
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
                throw new RoomRejectedException("The command payload could not be read: " + ex.Message,
                    Wire.Codes.BadPayload);
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
                if (_valueTaskAsTask is null)
                {
                    return returned;
                }

                Task asTask = (Task)_valueTaskAsTask.Invoke(returned, null)!;
                await asTask;
                return asTask.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(asTask);
        }
    }
}
