using GrainWire.Internal;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace GrainWire;

/// <summary>
/// Refuses to start a silo that contains a room grain which could run two operations at once.
/// </summary>
/// <param name="grainTypes">The grain classes registered in the silo.</param>
internal sealed class RoomGrainValidator(IOptions<GrainTypeOptions> grainTypes) : ILifecycleParticipant<ISiloLifecycle>
{
    /// <summary>
    /// Subscribes the room grain check to silo startup.
    /// </summary>
    /// <param name="lifecycle">The silo lifecycle.</param>
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(nameof(RoomGrainValidator), ServiceLifecycleStage.RuntimeInitialize, cancellationToken =>
        {
            foreach (Type grainClass in grainTypes.Value.Classes)
            {
                if (!IsRoomGrain(grainClass) || grainClass.IsAbstract)
                {
                    continue;
                }

                _ = RoomTypeInfo.For(grainClass);
            }

            return Task.CompletedTask;
        });
    }

    private static bool IsRoomGrain(Type type)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(RoomGrain<>))
            {
                return true;
            }
        }

        return false;
    }
}
