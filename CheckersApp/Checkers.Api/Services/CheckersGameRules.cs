using System.Text.RegularExpressions;

namespace Checkers.Api.Services;

public class CheckersGameRules
{
    public enum Piece : byte
    {
        Empty = 0,
        WhiteMan = 1,
        WhiteKing = 2,
        BlackMan = 3,
        BlackKing = 4
    }

    public record LegalMove(int FromSq, int ToSq, List<int> Path, bool IsJump, string Notation)
    {
        public override string ToString() => Notation;
    }

    public static (int x, int y) SqToCoord(int sq)
    {
        int n = sq - 1;
        int y = n / 4;
        int x = 2 * (3 - n % 4);
        if ((y % 2) != 0) x++;
        return (x, y);
    }

    public static int CoordToSq(int x, int y)
    {
        if (x < 0 || x >= 8 || y < 0 || y >= 8) return 0;
        return 4 * (y + 1) - x / 2;
    }

    public static (Piece[,] board, char turn, int pieceCount) ParsePdn(string pdn)
    {
        var board = new Piece[8, 8];
        int pieceCount = 0;

        var parts = pdn.Trim().Split(':');
        if (parts.Length < 3)
            throw new ArgumentException("Invalid PDN notation: must have at least 3 parts separated by colons.");

        char turn = char.ToUpperInvariant(parts[0].Trim()[0]);
        if (turn != 'W' && turn != 'B')
            throw new ArgumentException($"Invalid turn '{turn}'. Must be 'W' or 'B'.");

        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (string.IsNullOrEmpty(part)) continue;

            char side = char.ToUpperInvariant(part[0]);
            if (side != 'W' && side != 'B')
                throw new ArgumentException($"Invalid side '{side}'. Must be 'W' or 'B'.");

            string squareList = part.Substring(1);
            var tokens = squareList.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawToken in tokens)
            {
                string s = rawToken.Trim();
                bool isKing = s.StartsWith("K", StringComparison.OrdinalIgnoreCase);
                string numStr = isKing ? s.Substring(1) : s;

                if (!int.TryParse(numStr, out int sq) || sq < 1 || sq > 32)
                    throw new ArgumentException($"Invalid square '{s}'. Squares must be 1 to 32.");

                var (x, y) = SqToCoord(sq);
                if (board[x, y] != Piece.Empty)
                    throw new ArgumentException($"Square {sq} is occupied more than once.");

                if (side == 'W')
                    board[x, y] = isKing ? Piece.WhiteKing : Piece.WhiteMan;
                else
                    board[x, y] = isKing ? Piece.BlackKing : Piece.BlackMan;

                pieceCount++;
            }
        }

        if (pieceCount == 0)
            throw new ArgumentException("Board cannot be empty.");

        return (board, turn, pieceCount);
    }

    public static List<LegalMove> GenerateLegalMoves(string pdn)
    {
        var (board, turn, _) = ParsePdn(pdn);
        return GenerateLegalMoves(board, turn);
    }

    public static List<LegalMove> GenerateLegalMoves(Piece[,] board, char turn)
    {
        var jumpMoves = new List<LegalMove>();

        // 1. Search for all possible captures (jumps) first
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = SqToCoord(sq);
            var p = board[x, y];
            if (!IsPieceOfColor(p, turn)) continue;

            var currentPath = new List<int> { sq };
            FindJumpsRecursive(board, x, y, p, currentPath, jumpMoves);
        }

        // Under checkers rules, if any capture is available, capturing is MANDATORY
        if (jumpMoves.Count > 0)
        {
            return DeduplicateMoves(jumpMoves);
        }

        // 2. If no captures are available, generate standard quiet moves
        var quietMoves = new List<LegalMove>();
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = SqToCoord(sq);
            var p = board[x, y];
            if (!IsPieceOfColor(p, turn)) continue;

            var directions = GetMoveDirections(p);
            foreach (var (dx, dy) in directions)
            {
                int nx = x + dx;
                int ny = y + dy;
                if (nx >= 0 && nx < 8 && ny >= 0 && ny < 8 && board[nx, ny] == Piece.Empty)
                {
                    int toSq = CoordToSq(nx, ny);
                    if (toSq >= 1 && toSq <= 32)
                    {
                        quietMoves.Add(new LegalMove(sq, toSq, new List<int> { sq, toSq }, false, $"{sq}-{toSq}"));
                    }
                }
            }
        }

        return quietMoves;
    }

    private static void FindJumpsRecursive(
        Piece[,] currentBoard,
        int x,
        int y,
        Piece piece,
        List<int> path,
        List<LegalMove> results)
    {
        var directions = GetJumpDirections(piece);

        foreach (var (dx, dy) in directions)
        {
            int midX = x + dx;
            int midY = y + dy;
            int landX = x + 2 * dx;
            int landY = y + 2 * dy;

            if (landX < 0 || landX >= 8 || landY < 0 || landY >= 8) continue;
            if (currentBoard[landX, landY] != Piece.Empty) continue;

            var midPiece = currentBoard[midX, midY];
            if (midPiece == Piece.Empty || IsSameColor(piece, midPiece)) continue;

            // Valid jump destination
            int landSq = CoordToSq(landX, landY);

            // Clone board state for jump simulation
            var nextBoard = (Piece[,])currentBoard.Clone();
            nextBoard[x, y] = Piece.Empty;
            nextBoard[midX, midY] = Piece.Empty;

            // Check for king promotion
            bool crowned = false;
            Piece nextPiece = piece;
            if (piece == Piece.WhiteMan && landY == 0)
            {
                nextPiece = Piece.WhiteKing;
                crowned = true;
            }
            else if (piece == Piece.BlackMan && landY == 7)
            {
                nextPiece = Piece.BlackKing;
                crowned = true;
            }

            nextBoard[landX, landY] = nextPiece;

            var nextPath = new List<int>(path) { landSq };

            // In English draughts, if a man reaches the king row during a jump,
            // the move ends immediately (it crowns and cannot continue jumping on the same turn)
            if (crowned)
            {
                string notation = string.Join("x", nextPath);
                results.Add(new LegalMove(path[0], landSq, nextPath, true, notation));
            }
            else
            {
                // Check for subsequent jumps in multi-jump sequence
                int beforeCount = results.Count;
                FindJumpsRecursive(nextBoard, landX, landY, nextPiece, nextPath, results);

                // If no further jumps are possible, current sequence is complete
                if (results.Count == beforeCount)
                {
                    string notation = string.Join("x", nextPath);
                    results.Add(new LegalMove(path[0], landSq, nextPath, true, notation));
                }
            }
        }
    }

    public static bool IsLegalMove(string pdn, string moveStr)
    {
        if (string.IsNullOrWhiteSpace(pdn) || string.IsNullOrWhiteSpace(moveStr))
            return false;

        try
        {
            var legalMoves = GenerateLegalMoves(pdn);
            if (legalMoves.Count == 0) return false;

            // Parse numbers from move string: "22-18", "18x11", "22-18x11-7", "22x8", "22-8"
            var numbers = Regex.Matches(moveStr, @"\d+")
                .Select(m => int.Parse(m.Value))
                .ToList();

            if (numbers.Count < 2) return false;

            int fromSq = numbers[0];
            int toSq = numbers[^1];

            // 1. Exact match for From and To squares
            foreach (var lm in legalMoves)
            {
                if (lm.FromSq == fromSq && lm.ToSq == toSq)
                {
                    // If full multi-jump path was provided (e.g., 22-15-8 or 22x15x8), verify intermediate steps
                    if (numbers.Count == lm.Path.Count)
                    {
                        bool pathMatches = true;
                        for (int i = 0; i < numbers.Count; i++)
                        {
                            if (numbers[i] != lm.Path[i])
                            {
                                pathMatches = false;
                                break;
                            }
                        }
                        if (pathMatches) return true;
                    }
                    else if (numbers.Count == 2)
                    {
                        // Simplified start -> end notation is accepted for all valid moves/captures
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static (int dx, int dy)[] GetMoveDirections(Piece p) => p switch
    {
        Piece.WhiteMan => new[] { (-1, -1), (1, -1) },
        Piece.BlackMan => new[] { (-1, 1), (1, 1) },
        Piece.WhiteKing or Piece.BlackKing => new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) },
        _ => Array.Empty<(int, int)>()
    };

    private static (int dx, int dy)[] GetJumpDirections(Piece p) => p switch
    {
        Piece.WhiteMan => new[] { (-1, -1), (1, -1) },
        Piece.BlackMan => new[] { (-1, 1), (1, 1) },
        Piece.WhiteKing or Piece.BlackKing => new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) },
        _ => Array.Empty<(int, int)>()
    };

    private static bool IsPieceOfColor(Piece p, char color) => color switch
    {
        'W' => p == Piece.WhiteMan || p == Piece.WhiteKing,
        'B' => p == Piece.BlackMan || p == Piece.BlackKing,
        _ => false
    };

    private static bool IsSameColor(Piece p1, Piece p2)
    {
        bool isP1White = p1 == Piece.WhiteMan || p1 == Piece.WhiteKing;
        bool isP2White = p2 == Piece.WhiteMan || p2 == Piece.WhiteKing;
        return isP1White == isP2White;
    }

    private static List<LegalMove> DeduplicateMoves(List<LegalMove> moves)
    {
        var result = new List<LegalMove>();
        var seen = new HashSet<string>();
        foreach (var m in moves)
        {
            if (seen.Add(m.Notation))
            {
                result.Add(m);
            }
        }
        return result;
    }
}
