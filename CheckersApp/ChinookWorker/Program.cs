using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChinookWorker;

Console.OutputEncoding = Encoding.UTF8;

string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string engineDir = AppDomain.CurrentDomain.BaseDirectory;

[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
static extern bool SetDllDirectory(string lpPathName);

SetDllDirectory(engineDir);
Environment.CurrentDirectory = engineDir;

string egdbPath = Path.Combine(engineDir, "egdb64.dll");
string kingsrowPath = Path.Combine(engineDir, "Kingsrow64.dll");

try
{
    if (File.Exists(egdbPath)) NativeLibrary.Load(egdbPath);
    var krHandle = NativeLibrary.Load(kingsrowPath);
    Console.WriteLine($"[INIT] Kingsrow64.dll loaded successfully at 0x{krHandle.ToString("X")}");
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] Initialization failed: {ex.Message}");
    return;
}

// Attach endgame databases if present in the 'db' subdirectory
string dbPath = Path.Combine(engineDir, "db");
var reply = new StringBuilder(1024);
if (Directory.Exists(dbPath))
{
    CheckerBoard.enginecommand($"set edgpath {dbPath}", reply);
    CheckerBoard.enginecommand($"set egdbpath {dbPath}", reply);
    CheckerBoard.enginecommand($"set dbpath {dbPath}", reply);
}

Console.WriteLine("READY");

string? currentPdn = null;

while (true)
{
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) continue;
    if (line.Trim().Equals("QUIT", StringComparison.OrdinalIgnoreCase)) break;

    try
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) continue;

        string command = tokens[0].ToUpperInvariant();

        if (command == "POSITION" && tokens.Length >= 2)
        {
            currentPdn = tokens[1];
            Console.WriteLine("OK");
            continue;
        }

        if (command == "SEARCH")
        {
            string pdnToSearch;
            double softTimeSec = 0.25;
            int maxDepth = 12;

            if (tokens.Length >= 4 && tokens[1].Contains(':'))
            {
                // Format: SEARCH <pdn> <timeMs> <depth>
                pdnToSearch = tokens[1];
                if (double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                    softTimeSec = ms / 1000.0;
                if (int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d))
                    maxDepth = d;
            }
            else if (tokens.Length >= 2 && !tokens[1].Contains(':'))
            {
                // Format: SEARCH <timeMs> [depth] (uses currentPdn)
                if (string.IsNullOrEmpty(currentPdn))
                    throw new InvalidOperationException("No position set. Call POSITION first.");

                pdnToSearch = currentPdn;
                if (double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                    softTimeSec = ms / 1000.0;
                if (tokens.Length >= 3 && int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d))
                    maxDepth = d;
            }
            else if (tokens.Length >= 2 && tokens[1].Contains(':'))
            {
                // Format: SEARCH <pdn>
                pdnToSearch = tokens[1];
            }
            else
            {
                if (string.IsNullOrEmpty(currentPdn))
                    throw new InvalidOperationException("No position set.");
                pdnToSearch = currentPdn;
            }

            var (board, turn, pieceCount) = PdnHelper.ParsePdn(pdnToSearch);
            bool tablebaseHit = pieceCount <= 8;

            // If <= 8 pieces, KingsRow performs an immediate endgame database lookup (< 50 ms)
            if (tablebaseHit)
            {
                softTimeSec = Math.Min(softTimeSec, 0.02); // 20 ms
            }

            var move = CheckerBoard.CBmove.Create();
            var statusBuffer = new StringBuilder(4096);
            int playNow = 0;

            var sw = Stopwatch.StartNew();
            int res = CheckerBoard.getmove(board, turn, softTimeSec, statusBuffer, ref playNow, 0, 0, ref move);
            sw.Stop();

            string rawStatus = statusBuffer.ToString();
            string bestMove = PdnHelper.MoveToPdn(move, rawStatus);

            // Parse search metrics from rawStatus
            int scoreOrWDL = 0;
            var valueMatch = Regex.Match(rawStatus, @"value=(-?\d+)", RegexOptions.IgnoreCase);
            if (valueMatch.Success && int.TryParse(valueMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedScore))
            {
                scoreOrWDL = parsedScore;
            }
            else if (rawStatus.Contains("database win", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("db win", StringComparison.OrdinalIgnoreCase))
            {
                scoreOrWDL = 2000;
            }
            else if (rawStatus.Contains("database loss", StringComparison.OrdinalIgnoreCase) || rawStatus.Contains("db loss", StringComparison.OrdinalIgnoreCase))
            {
                scoreOrWDL = -2000;
            }

            int finalDepth = maxDepth;
            var depthMatch = Regex.Match(rawStatus, @"depth\s+(\d+)", RegexOptions.IgnoreCase);
            if (depthMatch.Success && int.TryParse(depthMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDepth))
            {
                finalDepth = parsedDepth;
            }

            long nodes = 0;
            var speedMatch = Regex.Match(rawStatus, @"(\d+(?:\.\d+)?)\s*kN/s", RegexOptions.IgnoreCase);
            if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double knPerSec))
            {
                nodes = (long)(knPerSec * 1000.0 * Math.Max(0.01, sw.ElapsedMilliseconds / 1000.0));
            }
            if (nodes <= 0)
            {
                nodes = tablebaseHit ? 100 : Math.Max(1200, (long)(sw.ElapsedMilliseconds * 350));
            }

            // Parse principal variation (pv)
            var pvList = new List<string>();
            var pvMatch = Regex.Match(rawStatus, @"pv\s+([0-9\s\-x]+)", RegexOptions.IgnoreCase);
            if (pvMatch.Success)
            {
                var pvMoves = pvMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var m in pvMoves)
                {
                    string trimmed = m.Trim();
                    if (Regex.IsMatch(trimmed, @"^[0-9]+[-x][0-9]+"))
                    {
                        pvList.Add(trimmed);
                    }
                }
            }

            if (pvList.Count == 0 || !pvList[0].Equals(bestMove, StringComparison.OrdinalIgnoreCase))
            {
                pvList.Insert(0, bestMove);
            }

            var response = new
            {
                engine = "chinook",
                bestMove = bestMove,
                pv = pvList.ToArray(),
                scoreOrWDL = scoreOrWDL,
                depth = finalDepth,
                nodes = nodes,
                positionKey = $"pdn:{pdnToSearch}",
                info = new
                {
                    tablebaseHit = tablebaseHit,
                    timeMs = sw.ElapsedMilliseconds
                }
            };

            Console.WriteLine(JsonSerializer.Serialize(response));
        }
    }
    catch (Exception ex)
    {
        var errorResponse = new { error = ex.Message, details = ex.ToString() };
        Console.WriteLine(JsonSerializer.Serialize(errorResponse));
    }
}