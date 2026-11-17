namespace GrainWire.Client.Protocol;

/// <summary>The outcome a reply reports.</summary>
public enum ReplyStatus
{
    /// <summary>Done and committed.</summary>
    Ok,

    /// <summary>Refused; nothing changed.</summary>
    Rejected,

    /// <summary>Could not be processed; nothing changed.</summary>
    Error,
}
