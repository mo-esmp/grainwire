namespace GrainWire;

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
