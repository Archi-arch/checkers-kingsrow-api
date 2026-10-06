using System.Text.Json;
using System.Text.Json.Serialization;

namespace Checkers.Api.Services;

public record AdapterSearchResult(
    string Engine,
    string BestMove,
    string[] Pv,
    int ScoreOrWDL,
    int Depth,
    long Nodes,
    string PositionKey,
    bool TablebaseHit,
    long TimeMs
);

public interface IEngineAdapter
{
    Task SetPositionAsync(string pdn, CancellationToken ct);
    Task<AdapterSearchResult> SearchAsync(int softTimeMs, int maxDepth, CancellationToken ct);
}

public class ChinookCliAdapter : IEngineAdapter
{
    private readonly WorkerProcess _worker;

    public ChinookCliAdapter(WorkerProcess worker)
    {
        _worker = worker;
    }

    public async Task SetPositionAsync(string pdn, CancellationToken ct)
    {
        await _worker.WriteLineAsync($"POSITION {pdn}", ct);
        string? ack = await _worker.ReadLineAsync(ct);
        if (ack == null || !ack.Contains("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Engine did not acknowledge position: {ack}");
        }
    }

    public async Task<AdapterSearchResult> SearchAsync(int softTimeMs, int maxDepth, CancellationToken ct)
    {
        await _worker.WriteLineAsync($"SEARCH {softTimeMs} {maxDepth}", ct);
        string? responseLine = await _worker.ReadLineAsync(ct);

        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("Engine returned an empty response.");
        }

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorProp))
        {
            string err = errorProp.GetString() ?? "Unknown engine error";
            throw new InvalidOperationException($"Engine search error: {err}");
        }

        string engine = root.GetProperty("engine").GetString() ?? "chinook";
        string bestMove = root.GetProperty("bestMove").GetString() ?? "";

        var pvList = new List<string>();
        if (root.TryGetProperty("pv", out var pvProp) && pvProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in pvProp.EnumerateArray())
            {
                string? m = elem.GetString();
                if (!string.IsNullOrEmpty(m)) pvList.Add(m);
            }
        }

        int scoreOrWDL = root.TryGetProperty("scoreOrWDL", out var sProp) ? sProp.GetInt32() : 0;
        int depth = root.TryGetProperty("depth", out var dProp) ? dProp.GetInt32() : maxDepth;
        long nodes = root.TryGetProperty("nodes", out var nProp) ? nProp.GetInt64() : 0;
        string positionKey = root.TryGetProperty("positionKey", out var pkProp) ? pkProp.GetString() ?? "" : "";

        bool tablebaseHit = false;
        long timeMs = 0;
        if (root.TryGetProperty("info", out var infoProp))
        {
            if (infoProp.TryGetProperty("tablebaseHit", out var tbProp))
                tablebaseHit = tbProp.GetBoolean();
            if (infoProp.TryGetProperty("timeMs", out var tmProp))
                timeMs = tmProp.GetInt64();
        }

        return new AdapterSearchResult(
            Engine: engine,
            BestMove: bestMove,
            Pv: pvList.ToArray(),
            ScoreOrWDL: scoreOrWDL,
            Depth: depth,
            Nodes: nodes,
            PositionKey: positionKey,
            TablebaseHit: tablebaseHit,
            TimeMs: timeMs
        );
    }
}
