namespace ChessFromScratch.BlazorClient.Game;

// What travels between this client and the chess room. The shapes match the records in
// Chess.Server/Game/ChessGameGrain.cs; on the wire they are JSON with camelCase names.

/// <summary>
/// The whole game as the server sends it in a snapshot: after joining, after a reconnect, and after catching up.
/// </summary>
public sealed record GameView(
    string Fen,
    string Turn,
    bool Check,
    string Status,
    string? White,
    string? Black,
    List<string> Moves,
    string? LastFrom,
    string? LastTo,
    long WhiteMs,
    long BlackMs,
    long InitialMs,
    DateTimeOffset? TurnStartedAt,
    string? Result,
    string? Reason,
    DateTimeOffset ServerNow);

/// <summary>
/// The second player sat down and white's clock started.
/// </summary>
public sealed record StartedEvent(string White, string Black, DateTimeOffset At);

/// <summary>
/// A move was played. It carries the new position, so the client never works out a position itself.
/// </summary>
public sealed record MovedEvent(
    string From,
    string To,
    string San,
    string Fen,
    string Turn,
    bool Check,
    long WhiteMs,
    long BlackMs,
    DateTimeOffset At);

/// <summary>
/// The game is over.
/// </summary>
public sealed record FinishedEvent(string Result, string Reason, long WhiteMs, long BlackMs);

/// <summary>
/// The payload of the <c>move</c> command.
/// </summary>
public sealed record MoveRequest(string From, string To, string? Promotion = null);

/// <summary>
/// The options sent when a game is created.
/// </summary>
public sealed record CreateGameOptions(int Minutes);

/// <summary>
/// The names the server uses for the two sides.
/// </summary>
public static class Seats
{
    public const string White = "white";
    public const string Black = "black";

    public static string Opponent(string seat) => seat == White ? Black : White;
}

/// <summary>
/// The names the server uses for the stages of a game.
/// </summary>
public static class GameStatus
{
    public const string Waiting = "waiting";
    public const string Playing = "playing";
    public const string Finished = "finished";
}
