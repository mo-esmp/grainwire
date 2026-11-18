using System.Collections.Concurrent;
using System.Reflection;
using Orleans.Concurrency;

namespace GrainWire.Internal;

/// <summary>
/// Reflection data about one room grain class, computed once.
/// </summary>
/// <remarks>
/// <para>
/// A room grain is a plain class deriving from <see cref="RoomGrain{TState}"/>. Everything the framework needs to
/// know about it — its room type name, which methods handle which commands, and which optional hooks it
/// overrides — is read here by reflection, once per class, and cached for the lifetime of the process.
/// Each grain activation then reads this shared instance instead of repeating the reflection.
/// </para>
/// <para>
/// Building an instance also validates the class: a missing <see cref="RoomTypeAttribute"/>, an invalid or
/// duplicate command name, a malformed handler signature, or any interleaving attribute throws
/// <see cref="InvalidOperationException"/>. <see cref="RoomGrainValidator"/> calls <see cref="For"/> for every
/// room grain at silo startup so these mistakes fail the silo on start rather than on the first activation.
/// </para>
/// </remarks>
internal sealed class RoomTypeInfo
{
    // One entry per room grain class. GetOrAdd may run the constructor more than once if two threads race on
    // the same class, but the result is the same either way and only one instance is kept.
    private static readonly ConcurrentDictionary<Type, RoomTypeInfo> s_cache = new();

    private RoomTypeInfo(Type grainClass)
    {
        // The room type name is the first half of a room address ("chess/abc123") and picks the Orleans grain type
        // ("gw-room-chess"), so every room grain must declare one. inherit: false — a subclass of a room grain is a
        // different room type and must name itself.
        RoomTypeAttribute attribute = grainClass.GetCustomAttribute<RoomTypeAttribute>(inherit: false)
                                      ?? throw new InvalidOperationException(
                                          $"{grainClass.FullName} derives from RoomGrain<TState> but has no [RoomType(\"...\")] attribute.");
        RoomType = attribute.Name;

        // Rooms rely on Orleans' one-call-at-a-time turn model to keep state, the operation log and broadcasts
        // consistent, so anything that lets calls interleave is rejected up front.
        EnsureNotInterleaving(grainClass);

        // Discover command handlers. Non-public methods are included so handlers can stay private to the room,
        // and GetMethods also returns public/protected methods inherited from base classes.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo method in grainClass.GetMethods(flags))
        {
            OnMessageAttribute? onMessage = method.GetCustomAttribute<OnMessageAttribute>(inherit: true);
            if (onMessage is null)
            {
                continue;
            }

            // Command names follow the same rules as a room type or id: 1–128 characters of [A-Za-z0-9_.~-].
            if (!RoomAddress.IsValidSegment(onMessage.Name))
            {
                throw new InvalidOperationException(
                    $"[OnMessage(\"{onMessage.Name}\")] on {grainClass.Name}.{method.Name} is not a valid command name.");
            }

            // Command names are case-sensitive (ordinal) and must map to exactly one method; dispatch would be
            // ambiguous otherwise. CommandHandler's constructor also validates the method's parameter list.
            if (!Handlers.TryAdd(onMessage.Name, new CommandHandler(method, onMessage)))
            {
                throw new InvalidOperationException(
                    $"{grainClass.Name} has more than one handler for command '{onMessage.Name}'.");
            }
        }

        // Record which optional hooks the room actually implements. RoomGrain<TState> checks these flags and
        // skips the hook entirely when it is not overridden (for OnRejoin, that means a returning member is let
        // back in without asking the room).
        OverridesOnRejoin = IsOverridden(grainClass, "OnRejoin");
        OverridesOnPresenceChanged = IsOverridden(grainClass, "OnPresenceChanged");
        OverridesOnPlayerLeft = IsOverridden(grainClass, "OnPlayerLeft");
    }

    /// <summary>
    /// The room type name from <see cref="RoomTypeAttribute"/>.
    /// </summary>
    public string RoomType { get; }

    /// <summary>
    /// Command handlers by command name.
    /// </summary>
    public Dictionary<string, CommandHandler> Handlers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// True when the grain class overrides <c>OnRejoin</c>.
    /// </summary>
    public bool OverridesOnRejoin { get; }

    /// <summary>
    /// True when the grain class overrides <c>OnPresenceChanged</c>.
    /// </summary>
    public bool OverridesOnPresenceChanged { get; }

    /// <summary>
    /// True when the grain class overrides <c>OnPlayerLeft</c>.
    /// </summary>
    public bool OverridesOnPlayerLeft { get; }

    /// <summary>
    /// Gets the reflection data of a room grain class, computing it on first use.
    /// </summary>
    /// <param name="grainClass">The room grain class.</param>
    /// <returns>The cached data.</returns>
    public static RoomTypeInfo For(Type grainClass) => s_cache.GetOrAdd(grainClass, static t => new RoomTypeInfo(t));

    /// <summary>
    /// Throws when <paramref name="grainClass"/> could run two room operations at once.
    /// </summary>
    /// <param name="grainClass">The room grain class to check.</param>
    /// <exception cref="InvalidOperationException">The class is reentrant or may interleave.</exception>
    public static void EnsureNotInterleaving(Type grainClass)
    {
        // Class-level opt-ins: [Reentrant] lets any call interleave at an await; [MayInterleave] lets a predicate
        // decide per call. Either would let two room operations overlap. inherit: true catches them on base classes.
        if (grainClass.IsDefined(typeof(ReentrantAttribute), inherit: true) ||
            grainClass.IsDefined(typeof(MayInterleaveAttribute), inherit: true))
        {
            throw new InvalidOperationException(
                $"{grainClass.FullName} is a room grain and must not be [Reentrant] or [MayInterleave]: rooms process one operation at a time.");
        }

        // Method-level opt-ins live on the grain interfaces (Orleans reads them there, not on the class), so check
        // every method of every interface the room implements — including ones the room author added.
        foreach (Type contract in grainClass.GetInterfaces())
        {
            foreach (MethodInfo method in contract.GetMethods())
            {
                if (method.IsDefined(typeof(AlwaysInterleaveAttribute), inherit: true) ||
                    method.IsDefined(typeof(ReadOnlyAttribute), inherit: true))
                {
                    throw new InvalidOperationException(
                        $"{contract.FullName}.{method.Name} is marked [AlwaysInterleave] or [ReadOnly], which {grainClass.FullName} (a room grain) does not allow: rooms process one operation at a time.");
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="grainClass"/> (or a base class between it and <see cref="RoomGrain{TState}"/>)
    /// overrides the virtual hook <paramref name="name"/>.
    /// </summary>
    private static bool IsOverridden(Type grainClass, string name)
    {
        // GetMethod on the concrete class returns the most-derived override of the virtual, and its DeclaringType
        // is the class that wrote that override. If that is still RoomGrain<TState> (a closed generic such as
        // RoomGrain<ChessState>, hence the comparison with the open definition), nobody overrode the hook.
        MethodInfo? method =
            grainClass.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Type? declaring = method?.DeclaringType;
        return declaring is not null &&
               !(declaring.IsGenericType && declaring.GetGenericTypeDefinition() == typeof(RoomGrain<>));
    }
}
