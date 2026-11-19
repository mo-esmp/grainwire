namespace ChessFromScratch.BlazorClient.Game;

/// <summary>
/// A piece on the board. <paramref name="Kind"/> is the lower-case FEN letter: k, q, r, b, n or p.
/// </summary>
public readonly record struct Piece(string Color, char Kind)
{
    public string Glyph => Kind switch
    {
        'k' => "♚",
        'q' => "♛",
        'r' => "♜",
        'b' => "♝",
        'n' => "♞",
        _ => "♟",
    };

    public string Name => Kind switch
    {
        'k' => "king",
        'q' => "queen",
        'r' => "rook",
        'b' => "bishop",
        'n' => "knight",
        _ => "pawn",
    };
}

/// <summary>
/// Reads where the pieces are from a FEN string. The client knows nothing else about chess: the server decides
/// what is legal.
/// </summary>
public static class Position
{
    public const string Files = "abcdefgh";

    /// <summary>
    /// Reads the piece placement of a FEN into a map from square name ("e2") to piece.
    /// </summary>
    public static IReadOnlyDictionary<string, Piece> Parse(string fen)
    {
        Dictionary<string, Piece> position = [];
        string[] rows = fen.Split(' ')[0].Split('/');
        for (int index = 0; index < rows.Length; index++)
        {
            int rank = 8 - index;
            int file = 0;
            foreach (char letter in rows[index])
            {
                if (char.IsDigit(letter))
                {
                    file += letter - '0';
                    continue;
                }

                if (file < Files.Length)
                {
                    char kind = char.ToLowerInvariant(letter);
                    position[$"{Files[file]}{rank}"] = new Piece(letter == kind ? Seats.Black : Seats.White, kind);
                }

                file++;
            }
        }

        return position;
    }
}
