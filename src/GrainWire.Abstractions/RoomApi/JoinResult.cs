namespace GrainWire;

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
