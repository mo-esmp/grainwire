namespace GrainWire;

/// <summary>
/// Marks a command handler. The method takes a <see cref="PlayerId"/> and optionally one payload parameter,
/// and returns nothing, a value, or a <see cref="Task"/> / <see cref="Task{TResult}"/>.
/// </summary>
/// <param name="name">The command name clients send.</param>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class OnMessageAttribute(string name) : Attribute
{
    /// <summary>
    /// The command name clients send.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Comma-separated roles allowed to send this command. Empty means every member.
    /// </summary>
    public string? Roles { get; set; }
}
