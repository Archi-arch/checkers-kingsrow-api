using System.Text;

namespace Checkers.Api.Services;

public interface IPdnService
{
    bool TryValidateAndCanonicalize(string rawPdn, out string canonicalPdn, out int pieceCount, out string? error);
}

public class PdnService : IPdnService
{
    public bool TryValidateAndCanonicalize(string rawPdn, out string canonicalPdn, out int pieceCount, out string? error)
    {
        canonicalPdn = string.Empty;
        pieceCount = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(rawPdn))
        {
            error = "PDN string cannot be empty.";
            return false;
        }

        var parts = rawPdn.Trim().Split(':');
        if (parts.Length < 3)
        {
            error = "PDN must contain at least 3 parts separated by colons (e.g. B:W...:B...).";
            return false;
        }

        string turn = parts[0].Trim().ToUpperInvariant();
        if (turn != "B" && turn != "W")
        {
            error = "Turn must be either 'B' or 'W'.";
            return false;
        }

        var whiteSquares = new SortedSet<int>();
        var whiteKings = new SortedSet<int>();
        var blackSquares = new SortedSet<int>();
        var blackKings = new SortedSet<int>();
        var allSquares = new HashSet<int>();

        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (string.IsNullOrEmpty(part)) continue;

            char color = char.ToUpperInvariant(part[0]);
            if (color != 'W' && color != 'B')
            {
                error = $"Unknown color section '{color}'. Must be 'W' or 'B'.";
                return false;
            }

            string squareList = part.Substring(1);
            var tokens = squareList.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawSquare in tokens)
            {
                string s = rawSquare.Trim();
                bool isKing = s.StartsWith("K", StringComparison.OrdinalIgnoreCase);
                string numStr = isKing ? s.Substring(1) : s;

                if (!int.TryParse(numStr, out int sq) || sq < 1 || sq > 32)
                {
                    error = $"Invalid square number '{s}'. Squares must be between 1 and 32.";
                    return false;
                }

                if (!allSquares.Add(sq))
                {
                    error = $"Square {sq} is occupied more than once.";
                    return false;
                }

                if (color == 'W')
                {
                    if (isKing) whiteKings.Add(sq);
                    else whiteSquares.Add(sq);
                }
                else
                {
                    if (isKing) blackKings.Add(sq);
                    else blackSquares.Add(sq);
                }

                pieceCount++;
            }
        }

        int whiteCount = whiteSquares.Count + whiteKings.Count;
        int blackCount = blackSquares.Count + blackKings.Count;

        if (pieceCount == 0)
        {
            error = "Board cannot be empty.";
            return false;
        }

        if (whiteCount > 12)
        {
            error = $"Invalid white piece count ({whiteCount}). Maximum allowed in 8x8 checkers is 12.";
            return false;
        }

        if (blackCount > 12)
        {
            error = $"Invalid black piece count ({blackCount}). Maximum allowed in 8x8 checkers is 12.";
            return false;
        }

        // Build canonical normalized PDN representation
        var sb = new StringBuilder();
        sb.Append(turn).Append(':');

        sb.Append('W');
        bool firstW = true;
        foreach (var k in whiteKings)
        {
            if (!firstW) sb.Append(',');
            sb.Append('K').Append(k);
            firstW = false;
        }
        foreach (var w in whiteSquares)
        {
            if (!firstW) sb.Append(',');
            sb.Append(w);
            firstW = false;
        }

        sb.Append(':');
        sb.Append('B');
        bool firstB = true;
        foreach (var k in blackKings)
        {
            if (!firstB) sb.Append(',');
            sb.Append('K').Append(k);
            firstB = false;
        }
        foreach (var b in blackSquares)
        {
            if (!firstB) sb.Append(',');
            sb.Append(b);
            firstB = false;
        }

        canonicalPdn = sb.ToString();
        return true;
    }
}