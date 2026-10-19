using System.Text.Json.Serialization;

namespace Checkers.Api.Models;

/// <summary>
/// Board state representation.
/// </summary>
/// <param name="Notation">Notation format, e.g. 'PDN'.</param>
/// <param name="Position">PDN position string, e.g. 'B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16'.</param>
public record StateDto(
    [property: JsonPropertyName("notation")] string Notation,
    [property: JsonPropertyName("position")] string Position
);

/// <summary>
/// Search calculation limits.
/// </summary>
/// <param name="MaxDepth">Maximum search ply depth.</param>
/// <param name="SoftTimeMs">Soft time limit in milliseconds.</param>
/// <param name="HardTimeMs">Hard timeout threshold in milliseconds.</param>
public record LimitsDto(
    [property: JsonPropertyName("maxDepth")] int? MaxDepth,
    [property: JsonPropertyName("softTimeMs")] int? SoftTimeMs,
    [property: JsonPropertyName("hardTimeMs")] int? HardTimeMs
);

/// <summary>
/// Move suggestion request payload.
/// </summary>
/// <param name="GameId">Game variant identifier, e.g. 'checkers-8x8'.</param>
/// <param name="State">Current board position state.</param>
/// <param name="Level">Difficulty level ('weak', 'medium', 'strong').</param>
/// <param name="Limits">Custom search limits (optional).</param>
public record SuggestRequest(
    [property: JsonPropertyName("gameId")] string GameId,
    [property: JsonPropertyName("state")] StateDto State,
    [property: JsonPropertyName("level")] string? Level,
    [property: JsonPropertyName("limits")] LimitsDto? Limits
);

/// <summary>
/// Additional move calculation metadata.
/// </summary>
/// <param name="TablebaseHit">Indicates if the move was resolved from the endgame database.</param>
/// <param name="TimeMs">Elapsed engine search time in milliseconds.</param>
public record SuggestResponseInfo(
    [property: JsonPropertyName("tablebaseHit")] bool TablebaseHit,
    [property: JsonPropertyName("timeMs")] long TimeMs
);

/// <summary>
/// Move suggestion response payload.
/// </summary>
/// <param name="Engine">Engine name ('chinook').</param>
/// <param name="BestMove">Optimal move in PDN notation (e.g. '14x23').</param>
/// <param name="Pv">Principal variation line of optimal moves.</param>
/// <param name="ScoreOrWDL">Evaluation score (centipawns or W/D/L status).</param>
/// <param name="Depth">Reached search depth.</param>
/// <param name="Nodes">Number of evaluated nodes.</param>
/// <param name="PositionKey">Canonical normalized PDN cache key.</param>
/// <param name="Info">Search performance info.</param>
public record SuggestResponse(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("bestMove")] string BestMove,
    [property: JsonPropertyName("pv")] string[] Pv,
    [property: JsonPropertyName("scoreOrWDL")] int ScoreOrWDL,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("nodes")] long Nodes,
    [property: JsonPropertyName("positionKey")] string PositionKey,
    [property: JsonPropertyName("info")] SuggestResponseInfo Info
);

/// <summary>
/// Move validation request payload.
/// </summary>
/// <param name="Position">Current board position in PDN format.</param>
/// <param name="Move">Move to validate (e.g. '14x23' or '10-15').</param>
public record ValidateRequest(
    [property: JsonPropertyName("position")] string Position,
    [property: JsonPropertyName("move")] string Move
);

/// <summary>
/// Move validation response payload.
/// </summary>
/// <param name="Legal">True if the move is legal under checkers rules; otherwise false.</param>
public record ValidateResponse(
    [property: JsonPropertyName("legal")] bool Legal
);

/// <summary>
/// Health check response payload.
/// </summary>
/// <param name="Ok">Indicates if the service and engine pool are healthy.</param>
/// <param name="Workers">Number of active background engine worker processes.</param>
public record HealthResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("workers")] int Workers
);