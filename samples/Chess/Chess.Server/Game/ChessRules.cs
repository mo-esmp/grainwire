using Chess;

namespace ChessFromScratch.Server.Game;

/// <summary>
/// The rules of chess, delegated to the Gera.Chess library (MIT). The game grain only asks "is this move legal, and what follows?".
/// </summary>
public static class ChessRules
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public sealed record MoveOutcome(string San, string Fen, string Turn, bool Check, string? Result, string? Reason);

    /// <summary>Plays a move on the position. Returns null when the move is not legal.</summary>
    public static MoveOutcome? TryMove(string fen, string? from, string? to, string? promotion)
    {
        if (from is not { Length: 2 } || to is not { Length: 2 })
        {
            return null;
        }

        ChessBoard board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.InsufficientMaterial | AutoEndgameRules.FiftyMoveRule);
        PromotionType promoteTo = promotion?.ToLowerInvariant() switch
        {
            "r" => PromotionType.ToRook,
            "b" => PromotionType.ToBishop,
            "n" => PromotionType.ToKnight,
            _ => PromotionType.ToQueen,
        };
        board.OnPromotePawn += (_, e) => e.PromotionResult = promoteTo;

        Move? chosen = null;
        foreach (Move candidate in board.Moves(allowAmbiguousCastle: false, generateSan: false))
        {
            if (Square(candidate.OriginalPosition) != from || Square(candidate.NewPosition) != to)
            {
                continue;
            }

            if (candidate.Parameter is MovePromotion offered && offered.PromotionType != PromotionType.Default && offered.PromotionType != promoteTo)
            {
                continue;
            }

            chosen = candidate;
            break;
        }

        if (chosen is null)
        {
            return null;
        }

        if (!board.Move(chosen))
        {
            return null;
        }

        Move played = board.ExecutedMoves[^1];
        string san = played.San ?? board.ParseToSan(played);
        string? result = null;
        string? reason = null;
        if (board.IsEndGame && board.EndGame is { } end)
        {
            (result, reason) = end.EndgameType switch
            {
                EndgameType.Checkmate => (end.WonSide == PieceColor.White ? Seats.White : Seats.Black, "checkmate"),
                EndgameType.Stalemate => ("draw", "stalemate"),
                EndgameType.InsufficientMaterial => ("draw", "insufficient material"),
                EndgameType.FiftyMoveRule => ("draw", "fifty-move rule"),
                _ => ("draw", "draw"),
            };
        }

        return new MoveOutcome(san, board.ToFen(), board.Turn == PieceColor.White ? Seats.White : Seats.Black, played.IsCheck, result, reason);
    }

    private static string Square(Position position) => $"{position.File()}{position.Rank()}";
}
