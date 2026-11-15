using ChessSample.Server;

namespace GrainWire.Tests;

/// <summary>The chess sample's use of its rules library: the cases the game grain depends on.</summary>
public class ChessSampleTests
{
    private static ChessRules.MoveOutcome Play(ref string fen, string from, string to, string? promotion = null)
    {
        var outcome = ChessRules.TryMove(fen, from, to, promotion);
        Assert.NotNull(outcome);
        fen = outcome.Fen;
        return outcome;
    }

    [Fact]
    public void Legal_moves_are_played_and_illegal_ones_are_refused()
    {
        var fen = ChessRules.StartFen;
        var first = Play(ref fen, "e2", "e4");
        Assert.Equal("e4", first.San);
        Assert.Equal(Seats.Black, first.Turn);
        Assert.Null(first.Result);

        Assert.Null(ChessRules.TryMove(fen, "e4", "e5", null));    // white's pawn, but it is black's turn
        Assert.Null(ChessRules.TryMove(fen, "a8", "a5", null));    // the rook cannot jump over its pawn
        Assert.Null(ChessRules.TryMove(fen, "zz", "e5", null));
        Assert.Null(ChessRules.TryMove(fen, null, "e5", null));
    }

    [Fact]
    public void Checkmate_ends_the_game_for_the_side_that_delivered_it()
    {
        var fen = ChessRules.StartFen;
        Play(ref fen, "f2", "f3");
        Play(ref fen, "e7", "e5");
        Play(ref fen, "g2", "g4");
        var mate = Play(ref fen, "d8", "h4");
        Assert.Equal((Seats.Black, "checkmate"), (mate.Result, mate.Reason));
        Assert.Contains("#", mate.San);
    }

    [Fact]
    public void Castling_is_entered_as_a_king_move()
    {
        var fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1";
        var castle = Play(ref fen, "e1", "g1");
        Assert.Equal("O-O", castle.San);
        Assert.StartsWith("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R4RK1 b kq", fen);
        Assert.Equal("O-O-O", Play(ref fen, "e8", "c8").San);
    }

    [Theory]
    [InlineData(null, 'Q')]
    [InlineData("q", 'Q')]
    [InlineData("n", 'N')]
    [InlineData("r", 'R')]
    public void A_pawn_promotes_to_the_chosen_piece_and_to_a_queen_by_default(string? promotion, char expected)
    {
        var fen = "8/P6k/8/8/8/8/8/K7 w - - 0 1";
        Play(ref fen, "a7", "a8", promotion);
        Assert.StartsWith($"{expected}7/7k", fen);
    }

    [Fact]
    public void En_passant_and_stalemate_are_handled()
    {
        var fen = "4k3/8/8/8/1p6/8/P7/4K3 w - - 0 1";
        Play(ref fen, "a2", "a4");
        var capture = Play(ref fen, "b4", "a3");
        Assert.StartsWith("4k3/8/8/8/8/p7/8/4K3 w", fen);
        Assert.Contains("x", capture.San);

        var almost = "7k/5Q2/8/8/8/8/8/K7 w - - 0 1";
        var stalemate = Play(ref almost, "f7", "g6");
        Assert.Equal(("draw", "stalemate"), (stalemate.Result, stalemate.Reason));
    }
}
