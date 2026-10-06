using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ChinookWorker;

public static class PdnHelper
{
    public static (int x, int y) NumberToCoor(int number)
    {
        int n = number - 1;
        int y = n / 4;
        int x = 2 * (3 - n % 4);
        if ((y % 2) != 0) x++;
        return (x, y);
    }

    public static int CoorToNumber(int x, int y)
    {
        return 4 * (y + 1) - x / 2;
    }

    public static (int[,] board, int turn, int pieceCount) ParsePdn(string pdn)
    {
        var board = new int[8, 8];
        int pieceCount = 0;

        var parts = pdn.Trim().Split(':');
        if (parts.Length < 3)
            throw new ArgumentException("Invalid PDN format");

        int turn = parts[0].Trim().ToUpper().StartsWith("W")
            ? CheckerBoard.CB_WHITE
            : CheckerBoard.CB_BLACK;

        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (string.IsNullOrEmpty(part)) continue;

            char side = char.ToUpper(part[0]);
            string squaresStr = part.Substring(1);
            var squares = squaresStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawSquare in squares)
            {
                string s = rawSquare.Trim();
                bool isKing = s.StartsWith("K", StringComparison.OrdinalIgnoreCase);
                string numStr = isKing ? s.Substring(1) : s;

                if (!int.TryParse(numStr, out int sqNum) || sqNum < 1 || sqNum > 32)
                    continue;

                var (x, y) = NumberToCoor(sqNum);

                if (side == 'W')
                    board[x, y] = isKing ? CheckerBoard.PIECE_WHITE_KING : CheckerBoard.PIECE_WHITE_MAN;
                else if (side == 'B')
                    board[x, y] = isKing ? CheckerBoard.PIECE_BLACK_KING : CheckerBoard.PIECE_BLACK_MAN;

                pieceCount++;
            }
        }

        return (board, turn, pieceCount);
    }

    public static string MoveToPdn(CheckerBoard.CBmove move, string statusStr)
    {
        int fromSq = CoorToNumber(move.from.x, move.from.y);
        int toSq = CoorToNumber(move.to.x, move.to.y);

        if (fromSq >= 1 && fromSq <= 32 && toSq >= 1 && toSq <= 32 && fromSq != toSq)
        {
            if (move.jumps > 1 && move.path != null)
            {
                var steps = new List<int> { fromSq };
                for (int i = 0; i < move.jumps && i < move.path.Length; i++)
                {
                    int pSq = CoorToNumber(move.path[i].x, move.path[i].y);
                    if (pSq >= 1 && pSq <= 32 && pSq != steps[^1])
                    {
                        steps.Add(pSq);
                    }
                }
                if (steps[^1] != toSq)
                {
                    steps.Add(toSq);
                }

                if (steps.Count > 1)
                {
                    return string.Join("x", steps);
                }
            }

            char sep = move.jumps > 0 ? 'x' : '-';
            return $"{fromSq}{sep}{toSq}";
        }

        var match = Regex.Match(statusStr, @"(?:pv|best)\s+([0-9]+[-x][0-9]+)", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value;

        var generalMatch = Regex.Match(statusStr, @"\b([1-9][0-9]?[-x][1-9][0-9]?)\b");
        if (generalMatch.Success)
            return generalMatch.Groups[1].Value;

        return "0-0";
    }
}