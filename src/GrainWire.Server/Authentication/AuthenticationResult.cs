namespace GrainWire.Server;

public sealed class AuthenticationResult
{
    private AuthenticationResult(bool succeeded, PlayerId player, IReadOnlyDictionary<string, string>? claims, string? failureReason)
    {
        Succeeded = succeeded;
        Player = player;
        Claims = claims ?? new Dictionary<string, string>();
        FailureReason = failureReason;
    }

    public bool Succeeded { get; }

    public PlayerId Player { get; }

    public IReadOnlyDictionary<string, string> Claims { get; }

    public string? FailureReason { get; }

    public static AuthenticationResult Success(PlayerId player, IReadOnlyDictionary<string, string>? claims = null) => new(true, player, claims, null);

    public static AuthenticationResult Fail(string reason) => new(false, default, null, reason);
}
