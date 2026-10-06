using System.Text.Json.Serialization;

namespace Checkers.Api.Models;

// Input models for POST /v1/move/suggest
public record StateDto(
    [property: JsonPropertyName("notation")] string Notation,
    [property: JsonPropertyName("position")] string Position
);

public record LimitsDto(
    [property: JsonPropertyName("maxDepth")] int? MaxDepth,
    [property: JsonPropertyName("softTimeMs")] int? SoftTimeMs,
    [property: JsonPropertyName("hardTimeMs")] int? HardTimeMs
);

public record SuggestRequest(
    [property: JsonPropertyName("gameId")] string GameId,
    [property: JsonPropertyName("state")] StateDto State,
    [property: JsonPropertyName("level")] string? Level,
    [property: JsonPropertyName("limits")] LimitsDto? Limits
);

// Output models for POST /v1/move/suggest
public record SuggestResponseInfo(
    [property: JsonPropertyName("tablebaseHit")] bool TablebaseHit,
    [property: JsonPropertyName("timeMs")] long TimeMs
);

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

// Models for POST /v1/move/validate
public record ValidateRequest(
    [property: JsonPropertyName("position")] string Position,
    [property: JsonPropertyName("move")] string Move
);

public record ValidateResponse(
    [property: JsonPropertyName("legal")] bool Legal
);

// Model for GET /healthz
public record HealthResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("workers")] int Workers
);