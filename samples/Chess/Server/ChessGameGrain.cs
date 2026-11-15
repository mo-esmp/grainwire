using GrainWire;

namespace ChessSample.Server;

/// <summary>
/// One chess game. This class is everything the sample needs on the server besides the rules of chess:
/// seats, turns, clocks, resigning, and what happens when a player does not come back.
/// GrainWire takes care of connections, ordering, presence, reconnects and saving.
/// </summary>
[RoomType("chess")]
public sealed class ChessGameGrain : RoomGrain<GameState>
{
    private const string Flag = "flag";

    protected override Task<CreateResult> OnCreate(CreateContext context)
    {
        var minutes = Math.Clamp(context.Options.As<CreateGameOptions>()?.Minutes ?? 10, 1, 180);
        State.InitialMs = State.WhiteMs = State.BlackMs = minutes * 60_000L;
        return Task.FromResult(CreateResult.Allow());
    }

    /// <summary>The first two players take white and black; everyone after that watches.</summary>
    protected override Task<JoinResult> OnJoin(JoinContext context)
    {
        if (State.White is null)
        {
            State.White = context.Player.Value;
            return Task.FromResult(JoinResult.Accept(Seats.White));
        }

        if (State.Black is null)
        {
            State.Black = context.Player.Value;
            State.Status = GameStatus.Playing;
            State.TurnStartedAt = Now;
            Schedule(Flag, TimeSpan.FromMilliseconds(State.WhiteMs));
            Broadcast("started", new StartedEvent(State.White, State.Black, Now));
            return Task.FromResult(JoinResult.Accept(Seats.Black));
        }

        return Task.FromResult(JoinResult.Accept(Seats.Spectator));
    }

    [OnMessage("move", Roles = "white,black")]
    public void Move(PlayerId player, MoveRequest request)
    {
        if (State.Status != GameStatus.Playing) Reject("The game is not in progress.", "not-playing");
        var mover = SeatOf(player);
        if (State.Turn != mover) Reject("It is not your turn.", "not-your-turn");

        var outcome = ChessRules.TryMove(State.Fen, request.From, request.To, request.Promotion);
        if (outcome is null) Reject("That move is not legal.", "illegal-move");

        // The mover's clock stops, the opponent's starts.
        var spent = (long)(Now - State.TurnStartedAt!.Value).TotalMilliseconds;
        if (mover == Seats.White) State.WhiteMs = Math.Max(0, State.WhiteMs - spent);
        else State.BlackMs = Math.Max(0, State.BlackMs - spent);

        State.Fen = outcome.Fen;
        State.Turn = outcome.Turn;
        State.Moves.Add(outcome.San);
        State.LastFrom = request.From;
        State.LastTo = request.To;
        State.TurnStartedAt = Now;
        Broadcast("moved", new MovedEvent(request.From, request.To, outcome.San, outcome.Fen, outcome.Turn, outcome.Check, State.WhiteMs, State.BlackMs, Now));

        if (outcome.Result is not null) Finish(outcome.Result, outcome.Reason!);
        else Schedule(Flag, TimeSpan.FromMilliseconds(mover == Seats.White ? State.BlackMs : State.WhiteMs));
    }

    [OnMessage("resign", Roles = "white,black")]
    public void Resign(PlayerId player)
    {
        if (State.Status != GameStatus.Playing) Reject("The game is not in progress.", "not-playing");
        Finish(Seats.Opponent(SeatOf(player)), "resignation");
    }

    /// <summary>The clock of the side to move ran out. A deadline survives a server restart because the room is persistent.</summary>
    protected override Task OnDeadline(string name)
    {
        if (name != Flag || State.Status != GameStatus.Playing) return Task.CompletedTask;
        if (State.Turn == Seats.White) State.WhiteMs = 0;
        else State.BlackMs = 0;
        Finish(Seats.Opponent(State.Turn), "timeout");
        return Task.CompletedTask;
    }

    /// <summary>A player dropped and did not come back within the grace period: the opponent wins.</summary>
    protected override Task OnPlayerLeft(PlayerId player, LeaveReason reason)
    {
        if (State.Status == GameStatus.Playing && (player.Value == State.White || player.Value == State.Black))
        {
            Finish(Seats.Opponent(SeatOf(player)), "abandoned");
        }

        return Task.CompletedTask;
    }

    /// <summary>Chess has no hidden information, so every viewer sees the same thing.</summary>
    protected override object GetSnapshot(PlayerId viewer) => new GameView(
        State.Fen, State.Turn, State.Status, State.White, State.Black, State.Moves, State.LastFrom, State.LastTo,
        State.WhiteMs, State.BlackMs, State.InitialMs, State.TurnStartedAt, State.Result, State.Reason, Now);

    private void Finish(string result, string reason)
    {
        State.Status = GameStatus.Finished;
        State.Result = result;
        State.Reason = reason;
        State.TurnStartedAt = null;
        CancelSchedule(Flag);
        Broadcast("finished", new FinishedEvent(result, reason, State.WhiteMs, State.BlackMs));
    }

    private string SeatOf(PlayerId player) => player.Value == State.White ? Seats.White : Seats.Black;
}

public static class Seats
{
    public const string White = "white";
    public const string Black = "black";
    public const string Spectator = "spectator";

    public static string Opponent(string seat) => seat == White ? Black : White;
}

public static class GameStatus
{
    public const string Waiting = "waiting";
    public const string Playing = "playing";
    public const string Finished = "finished";
}

/// <summary>What is saved for a game.</summary>
[GenerateSerializer]
public sealed class GameState
{
    [Id(0)] public string Fen { get; set; } = ChessRules.StartFen;
    [Id(1)] public string Turn { get; set; } = Seats.White;
    [Id(2)] public string Status { get; set; } = GameStatus.Waiting;
    [Id(3)] public string? White { get; set; }
    [Id(4)] public string? Black { get; set; }
    [Id(5)] public List<string> Moves { get; set; } = new();
    [Id(6)] public string? LastFrom { get; set; }
    [Id(7)] public string? LastTo { get; set; }
    [Id(8)] public long WhiteMs { get; set; }
    [Id(9)] public long BlackMs { get; set; }
    [Id(10)] public long InitialMs { get; set; }
    [Id(11)] public DateTimeOffset? TurnStartedAt { get; set; }
    [Id(12)] public string? Result { get; set; }
    [Id(13)] public string? Reason { get; set; }
}

public sealed record CreateGameOptions(int? Minutes);

public sealed record MoveRequest(string From, string To, string? Promotion = null);

public sealed record GameView(
    string Fen, string Turn, string Status, string? White, string? Black, List<string> Moves, string? LastFrom, string? LastTo,
    long WhiteMs, long BlackMs, long InitialMs, DateTimeOffset? TurnStartedAt, string? Result, string? Reason, DateTimeOffset ServerNow);

public sealed record StartedEvent(string White, string Black, DateTimeOffset At);

public sealed record MovedEvent(string From, string To, string San, string Fen, string Turn, bool Check, long WhiteMs, long BlackMs, DateTimeOffset At);

public sealed record FinishedEvent(string Result, string Reason, long WhiteMs, long BlackMs);
