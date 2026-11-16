namespace GrainWire;

public sealed class CreateResult
{
    private CreateResult(bool allowed, string? reason)
    {
        Allowed = allowed;
        Reason = reason;
    }

    public bool Allowed { get; }
    public string? Reason { get; }

    public static CreateResult Allow() => new(true, null);
    public static CreateResult Deny(string reason) => new(false, reason);
}
