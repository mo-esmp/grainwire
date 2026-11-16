namespace GrainWire;

/// <summary>How a room undoes state changes made by a handler that is rejected or throws.</summary>
public enum RollbackMode
{
    /// <summary>Copy the state before every handler and restore the copy on failure. Safe default.</summary>
    Copy = 0,
    /// <summary>No copy. Use only when state is large and every handler validates before it changes anything.</summary>
    None = 1,
}
