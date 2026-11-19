using ChessFromScratch.BlazorClient.Services;
using GrainWire.Client;

namespace ChessFromScratch.BlazorClient.Game;

/// <summary>
/// One game as this player sees it. It joins the room, keeps a copy of the game from the server's snapshot,
/// applies the events that follow, and sends the player's commands. The page only draws what is here.
/// Everything about connections, ordering and catching up is done by GrainWire.Client.
/// </summary>
public sealed class GameSession(ChessConnection connection) : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, Piece> s_noPieces = new Dictionary<string, Piece>();

    private readonly List<IDisposable> _subscriptions = [];
    private Room<GameView>? _room;
    private TimeSpan _clockOffset; // server time minus this computer's time
    private bool _disposed;

    /// <summary>
    /// Called whenever something the page shows has changed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The game, or null until the first snapshot has arrived.
    /// </summary>
    public GameView? Game { get; private set; }

    /// <summary>
    /// Where the pieces are, by square name.
    /// </summary>
    public IReadOnlyDictionary<string, Piece> Pieces { get; private set; } = s_noPieces;

    /// <summary>
    /// The square of the piece the player picked with a click, if any.
    /// </summary>
    public string? Selected { get; private set; }

    /// <summary>
    /// True when the side to move is in check.
    /// </summary>
    public bool Check => Game?.Check ?? false;

    /// <summary>
    /// A short message for the player, such as why a move was refused. It clears itself after a few seconds.
    /// </summary>
    public string Notice { get; private set; } = "";

    /// <summary>
    /// Set when the room ended for this client.
    /// </summary>
    public string ClosedMessage { get; private set; } = "";

    /// <summary>
    /// Why the game could not be opened, or null.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// "white" or "black" for the two players, null for someone who watches.
    /// </summary>
    public string? Seat => _room?.You?.Role switch
    {
        Seats.White => Seats.White,
        Seats.Black => Seats.Black,
        _ => null,
    };

    /// <summary>
    /// The name this player has in the room.
    /// </summary>
    public string? Me => _room?.You?.Player;

    public ConnectionState Connection => connection.State;

    /// <summary>
    /// True when it is this player's turn and a move can be sent right now.
    /// </summary>
    public bool CanMove =>
        Game is { Status: GameStatus.Playing } game
        && _room is { IsClosed: false }
        && game.Turn == Seat
        && Connection == ConnectionState.Connected;

    /// <summary>
    /// Joins the game, creating it first when <paramref name="createWithMinutes"/> is given.
    /// </summary>
    /// <returns>False when the game could not be opened; <see cref="Error"/> then says why.</returns>
    public async Task<bool> OpenAsync(string id, int? createWithMinutes)
    {
        Room<GameView> room;
        try
        {
            GrainWireClient client = await connection.GetAsync();
            room = await client.JoinAsync<GameView>("chess", id, new JoinOptions
            {
                Create = createWithMinutes is not null,
                Options = createWithMinutes is { } minutes ? new CreateGameOptions(minutes) : null,
            });
        }
        catch (Exception error) when (error is GrainWireClientException or ArgumentException)
        {
            Error = Describe(error, "This game does not exist.");
            Notify();
            return false;
        }

        if (_disposed)
        {
            // The player left the page while the game was being joined.
            await LeaveAsync(room);
            return false;
        }

        _room = room;
        connection.StateChanged += Notify;

        // A snapshot is "this is the game now". It arrives after joining, after a reconnect, and after catching up.
        _subscriptions.Add(room.OnSnapshot((view, _) =>
        {
            if (view is null)
            {
                return;
            }

            // The list is copied, because the events that follow are applied to it.
            Show(view with { Moves = [.. view.Moves] });
            _clockOffset = view.ServerNow - DateTimeOffset.UtcNow;
            Selected = null;
            Notify();
        }));

        _subscriptions.Add(room.On<StartedEvent>("started", started =>
        {
            if (Game is null)
            {
                return;
            }

            Show(Game with { Status = GameStatus.Playing, White = started.White, Black = started.Black, TurnStartedAt = started.At });
            Notify();
        }));

        _subscriptions.Add(room.On<MovedEvent>("moved", moved =>
        {
            if (Game is null)
            {
                return;
            }

            Game.Moves.Add(moved.San);
            Show(Game with
            {
                Fen = moved.Fen,
                Turn = moved.Turn,
                Check = moved.Check,
                LastFrom = moved.From,
                LastTo = moved.To,
                WhiteMs = moved.WhiteMs,
                BlackMs = moved.BlackMs,
                TurnStartedAt = moved.At,
            });
            Selected = null;
            Notify();
        }));

        _subscriptions.Add(room.On<FinishedEvent>("finished", finished =>
        {
            if (Game is null)
            {
                return;
            }

            Show(Game with
            {
                Status = GameStatus.Finished,
                Result = finished.Result,
                Reason = finished.Reason,
                WhiteMs = finished.WhiteMs,
                BlackMs = finished.BlackMs,
                TurnStartedAt = null,
            });
            Selected = null;
            Notify();
        }));

        _subscriptions.Add(room.OnPresence(_ => Notify()));

        _subscriptions.Add(room.OnClosed(closed =>
        {
            ClosedMessage = closed.Reason == RoomClosedReason.Lost ? "This game was lost on the server." : "This game was closed.";
            Notify();
        }));

        return true;
    }

    /// <summary>
    /// How a player is doing: online, reconnecting, or gone. Null when the seat is still empty.
    /// </summary>
    public MemberStatus? PresenceOf(string? player)
    {
        if (player is null)
        {
            return null;
        }

        return _room?.Members.FirstOrDefault(member => member.Player == player)?.Status ?? MemberStatus.Left;
    }

    /// <summary>
    /// The time a side has left, in milliseconds. The clock of the side to move runs down between events,
    /// measured against the server's time.
    /// </summary>
    public long Remaining(string seat)
    {
        if (Game is not { } game)
        {
            return 0;
        }

        long left = seat == Seats.White ? game.WhiteMs : game.BlackMs;
        if (game.Status != GameStatus.Playing || game.Turn != seat || game.TurnStartedAt is not { } startedAt)
        {
            return left;
        }

        double spent = Math.Max(0, (DateTimeOffset.UtcNow + _clockOffset - startedAt).TotalMilliseconds);
        return Math.Max(0, left - (long)spent);
    }

    /// <summary>
    /// The player clicked a square: the first click picks one of their pieces, the second says where it goes.
    /// </summary>
    public async Task ClickAsync(string square)
    {
        if (!CanMove)
        {
            return;
        }

        bool mine = IsMine(square);
        if (Selected is { } from && from != square && !mine)
        {
            await MoveAsync(from, square);
            return;
        }

        Selected = mine && Selected != square ? square : null;
        Notify();
    }

    /// <summary>
    /// Asks the server to play a move. Used by the second click and by dropping a dragged piece.
    /// A move onto one of the player's own pieces is not sent. A pawn that reaches the last rank becomes a queen.
    /// </summary>
    public async Task MoveAsync(string from, string to)
    {
        if (!CanMove || from == to || !IsMine(from) || IsMine(to))
        {
            return;
        }

        Selected = null;
        bool promotes = Pieces[from].Kind == 'p' && (to.EndsWith('8') || to.EndsWith('1'));
        await CommandAsync("move", new MoveRequest(from, to, promotes ? "q" : null));
    }

    public Task ResignAsync() => CommandAsync("resign");

    /// <summary>
    /// Shows a message for a few seconds.
    /// </summary>
    public void Flash(string message)
    {
        Notice = message;
        Notify();
        _ = ClearNoticeLaterAsync(message);
    }

    /// <summary>
    /// Stops following the game. The player stays a member of the room until the server's grace period ends.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        connection.StateChanged -= Notify;
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        if (_room is { } room)
        {
            await LeaveAsync(room);
        }
    }

    private static async Task LeaveAsync(Room room)
    {
        try
        {
            await room.LeaveAsync();
        }
        catch (GrainWireClientException)
        {
            // Offline, or the room is already closed: there is nothing left to leave.
        }
    }

    private static string Describe(Exception error, string fallback) => error switch
    {
        RejectedException rejected => rejected.Reason ?? fallback,
        NotConnectedException => "You are offline. Reconnecting…",
        RoomException { Code: "room-not-found" } => "This game does not exist.",
        ConnectException => "The chess server cannot be reached. Is it running?",
        _ => string.IsNullOrEmpty(error.Message) ? fallback : error.Message,
    };

    private bool IsMine(string square) => Seat is { } seat && Pieces.TryGetValue(square, out Piece piece) && piece.Color == seat;

    private void Show(GameView game)
    {
        Game = game;
        Pieces = Position.Parse(game.Fen);
    }

    private async Task CommandAsync(string name, object? data = null)
    {
        if (_room is not { } room)
        {
            return;
        }

        try
        {
            await room.RequestAsync(name, data);
        }
        catch (OutcomeUnknownException)
        {
            // No answer in time: the move may or may not have been played. Ask the server for the current game.
            Flash("No answer from the server. Checking the game…");
            try
            {
                await room.SyncAsync();
            }
            catch (GrainWireClientException)
            {
                // Still no connection. The client keeps trying, and a snapshot follows when it is back.
            }
        }
        catch (GrainWireClientException error)
        {
            Flash(Describe(error, "That did not work."));
        }

        Notify();
    }

    private async Task ClearNoticeLaterAsync(string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (!_disposed && Notice == message)
        {
            Notice = "";
            Notify();
        }
    }

    private void Notify() => Changed?.Invoke();
}
