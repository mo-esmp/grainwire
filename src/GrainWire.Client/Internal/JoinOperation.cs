namespace GrainWire.Client.Internal;

/// <summary>A join that is still under way. Callers that ask for the same room meanwhile wait for the same outcome.</summary>
internal sealed class JoinOperation(Room room)
{
    public Room Room { get; } = room;

    public TaskCompletionSource<Room> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
