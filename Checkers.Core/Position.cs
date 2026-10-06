using System.Numerics;

namespace Checkers.Core;

public enum Side { Black, White }

public sealed class PdnException(string message) : Exception(message);

/// <summary>A move; <see cref="Path"/> holds every landing square, so a multi-jump is "14x23x32".</summary>
public sealed record Move(int From, int[] Path, uint Captured, bool Promotes)
{
    public int To => Path[^1];
    public override string ToString() => Captured != 0 ? $"{From}x{string.Join("x", Path)}" : $"{From}-{To}";
}

/// <summary>
/// English checkers position (bit 0 = square 1). Black starts on 1..12 and moves first; men move and capture
/// forward only, kings one square in any direction; capturing is mandatory, multi-jumps must be completed,
/// and a man that is crowned ends the move.
/// </summary>
public sealed record Position(uint Black, uint White, uint Kings, Side SideToMove)
{
    public static readonly Position Initial = new(0x00000FFFu, 0xFFF00000u, 0, Side.Black);

    // Diagonal neighbours: directions 0, 1 lead to higher squares (black men), 2, 3 to lower squares (white men).
    private static readonly int[,] Next = BuildNext();

    public int PieceCount => BitOperations.PopCount(Black | White);

    public static uint Bit(int square) => 1u << (square - 1);

    public static IEnumerable<int> Squares(uint mask) => Enumerable.Range(1, 32).Where(s => (mask & Bit(s)) != 0);

    /// <summary>CheckerBoard board coordinates of a square (square 1 is x = 6, y = 0).</summary>
    public static (int X, int Y) Coords(int square)
    {
        int n = square - 1, y = n / 4;
        return (2 * (3 - n % 4) + y % 2, y);
    }

    private uint Pieces(Side side) => side == Side.Black ? Black : White;

    private static Side Other(Side side) => side == Side.Black ? Side.White : Side.Black;

    private static bool IsCrowning(int square, Side side) => side == Side.Black ? square >= 29 : square <= 4;

    private static int[] Directions(bool king, Side side) => king ? [0, 1, 2, 3] : side == Side.Black ? [0, 1] : [2, 3];

    public static Position Parse(string? fen)
    {
        var parts = (fen ?? "").Trim().ToUpperInvariant().Split(':');
        if (parts.Length != 3 || parts[0] is not ("B" or "W") || parts[1].Length == 0 || parts[2].Length == 0
            || parts[1][0] is not ('W' or 'B') || parts[2][0] is not ('W' or 'B') || parts[1][0] == parts[2][0])
            throw new PdnException($"Expected '<B|W>:W<squares>:B<squares>', got '{fen}'.");

        uint black = 0, white = 0, kings = 0;
        foreach (var part in parts[1..])
        {
            var side = part[0] == 'B' ? Side.Black : Side.White;
            foreach (var token in part[1..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                bool king = token[0] == 'K';
                if (!int.TryParse(king ? token[1..] : token, out int square) || square is < 1 or > 32)
                    throw new PdnException($"Invalid square '{token}', expected 1..32.");
                if (((black | white) & Bit(square)) != 0) throw new PdnException($"Square {square} is occupied twice.");
                if (!king && IsCrowning(square, side)) throw new PdnException($"{side} man on square {square} must be a king.");
                if (side == Side.Black) black |= Bit(square); else white |= Bit(square);
                if (king) kings |= Bit(square);
            }
        }
        if (BitOperations.PopCount(black) > 12 || BitOperations.PopCount(white) > 12)
            throw new PdnException("At most 12 pieces per side.");
        return new Position(black, white, kings, parts[0] == "B" ? Side.Black : Side.White);
    }

    /// <summary>Canonical PDN: side to move, white pieces, black pieces, squares ascending.</summary>
    public string ToFen() => $"{(SideToMove == Side.Black ? 'B' : 'W')}:W{List(White)}:B{List(Black)}";

    private string List(uint pieces) => string.Join(",", Squares(pieces).Select(s => ((Kings & Bit(s)) != 0 ? "K" : "") + s));

    public List<Move> Moves()
    {
        var moves = new List<Move>();
        foreach (var from in Squares(Pieces(SideToMove)))
            Jump(from, from, (Kings & Bit(from)) != 0, (Black | White) & ~Bit(from), 0, [], moves);
        if (moves.Count > 0) return moves; // captures are mandatory

        foreach (var from in Squares(Pieces(SideToMove)))
        {
            bool king = (Kings & Bit(from)) != 0;
            foreach (var d in Directions(king, SideToMove))
            {
                int to = Next[from, d];
                if (to != 0 && ((Black | White) & Bit(to)) == 0)
                    moves.Add(new Move(from, [to], 0, !king && IsCrowning(to, SideToMove)));
            }
        }
        return moves;
    }

    // Jumped pieces stay on the board until the move ends: they block landing squares and cannot be jumped twice.
    private void Jump(int from, int at, bool king, uint occupied, uint captured, List<int> path, List<Move> moves)
    {
        bool extended = false;
        foreach (var d in Directions(king, SideToMove))
        {
            int over = Next[at, d], land = over == 0 ? 0 : Next[over, d];
            if (land == 0 || (Pieces(Other(SideToMove)) & ~captured & Bit(over)) == 0 || (occupied & Bit(land)) != 0) continue;
            extended = true;
            path.Add(land);
            if (!king && IsCrowning(land, SideToMove)) moves.Add(new Move(from, [.. path], captured | Bit(over), true));
            else Jump(from, land, king, occupied, captured | Bit(over), path, moves);
            path.RemoveAt(path.Count - 1);
        }
        if (!extended && captured != 0) moves.Add(new Move(from, [.. path], captured, false));
    }

    /// <summary>True if <paramref name="side"/> could capture, whoever is to move.</summary>
    public bool HasCapture(Side side) => Squares(Pieces(side)).Any(from =>
        Directions((Kings & Bit(from)) != 0, side).Any(d =>
        {
            int over = Next[from, d], land = over == 0 ? 0 : Next[over, d];
            return land != 0 && (Pieces(Other(side)) & Bit(over)) != 0 && ((Black | White) & Bit(land)) == 0;
        }));

    /// <summary>The legal move written as "22-18", "14x23" or "14x23x32" (from/to is enough), or null.</summary>
    public Move? Find(string? notation)
    {
        var squares = (notation ?? "").Split('-', 'x', 'X').Select(t => int.TryParse(t, out var n) ? n : 0).ToArray();
        if (squares.Length < 2 || squares.Any(s => s is < 1 or > 32)) throw new PdnException($"Invalid move '{notation}'.");
        return Moves().FirstOrDefault(m => m.From == squares[0] && m.To == squares[^1]
                                           && (squares.Length == 2 || m.Path.SequenceEqual(squares[1..])));
    }

    public Position Apply(Move move)
    {
        uint from = Bit(move.From), to = Bit(move.To);
        uint own = (Pieces(SideToMove) & ~from) | to, enemy = Pieces(Other(SideToMove)) & ~move.Captured;
        uint kings = (Kings & ~from & ~move.Captured) | ((Kings & from) != 0 || move.Promotes ? to : 0);
        return SideToMove == Side.Black ? new(own, enemy, kings, Side.White) : new(enemy, own, kings, Side.Black);
    }

    private static int[,] BuildNext()
    {
        static int At(int x, int y) => x is < 0 or > 7 || y is < 0 or > 7 ? 0 : 4 * (y + 1) - x / 2;
        var next = new int[33, 4];
        for (int s = 1; s <= 32; s++)
        {
            var (x, y) = Coords(s);
            (next[s, 0], next[s, 1], next[s, 2], next[s, 3]) = (At(x - 1, y + 1), At(x + 1, y + 1), At(x - 1, y - 1), At(x + 1, y - 1));
        }
        return next;
    }
}
