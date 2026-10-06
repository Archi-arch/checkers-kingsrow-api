using System.Diagnostics;
using System.Text.Json;
using Checkers.Api.Models;
using Checkers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Checkers.Api.Controllers;

[ApiController]
public class MoveController : ControllerBase
{
    private readonly IEnginePool _enginePool;
    private readonly IPdnService _pdnService;
    private readonly ILruCacheService _cache;
    private readonly ILogger<MoveController> _logger;
    private readonly IConfiguration _config;

    public MoveController(
        IEnginePool enginePool,
        IPdnService pdnService,
        ILruCacheService cache,
        ILogger<MoveController> logger,
        IConfiguration config)
    {
        _enginePool = enginePool;
        _pdnService = pdnService;
        _cache = cache;
        _logger = logger;
        _config = config;
    }

    [HttpGet("healthz")]
    public IActionResult Health()
    {
        var response = new HealthResponse(
            Ok: _enginePool.IsHealthy,
            Workers: _enginePool.ActiveWorkersCount
        );
        return Ok(response);
    }

    [HttpPost("v1/move/suggest")]
    public async Task<IActionResult> Suggest([FromBody] SuggestRequest request)
    {
        var sw = Stopwatch.StartNew();
        string requestId = HttpContext.TraceIdentifier;

        // 1. PDN validation and canonicalization (422 on invalid notation or piece count)
        string rawPdn = request.State?.Position ?? string.Empty;
        if (!_pdnService.TryValidateAndCanonicalize(rawPdn, out string canonicalPdn, out int pieceCount, out string? error))
        {
            return StatusCode(422, new { error = error ?? "Invalid PDN notation." });
        }

        string cacheKey = $"pdn:{canonicalPdn}";

        // 2. LRU cache lookup
        if (_cache.TryGet(cacheKey, out var cachedResponse) && cachedResponse != null)
        {
            LogJsonMetrics(requestId, sw.ElapsedMilliseconds, cachedResponse.Depth, cachedResponse.Nodes, cachedResponse.Info.TablebaseHit);
            return Ok(cachedResponse);
        }

        // 3. Determine level limits and check for <= 8 pieces (Tablebase Probe)
        int defaultSoft = _config.GetValue<int>("Limits:DefaultSoftTimeMs", 300);
        int defaultHard = _config.GetValue<int>("Limits:DefaultHardTimeMs", 1200);

        int depth;
        int softTimeMs;
        int hardTimeMs = request.Limits?.HardTimeMs ?? defaultHard;

        bool isTablebasePosition = pieceCount <= 8;

        if (isTablebasePosition)
        {
            // If <= 8 pieces, fast tablebase probe (< 50 ms)
            depth = 6;
            softTimeMs = Math.Min(request.Limits?.SoftTimeMs ?? 50, 40);
        }
        else
        {
            string level = (request.Level ?? "medium").ToLowerInvariant();
            switch (level)
            {
                case "weak":
                    depth = request.Limits?.MaxDepth ?? 8;
                    softTimeMs = request.Limits?.SoftTimeMs ?? 100;
                    break;
                case "strong":
                    depth = request.Limits?.MaxDepth ?? 16;
                    softTimeMs = request.Limits?.SoftTimeMs ?? 550;
                    break;
                case "medium":
                default:
                    depth = request.Limits?.MaxDepth ?? 12;
                    softTimeMs = request.Limits?.SoftTimeMs ?? 250;
                    break;
            }
        }

        // 4. Invoke worker with Hard Timeout safeguard (504 on timeout)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted,
            new CancellationTokenSource(TimeSpan.FromMilliseconds(hardTimeMs)).Token
        );

        try
        {
            var response = await _enginePool.SuggestMoveAsync(canonicalPdn, softTimeMs, depth, cts.Token);

            // If piece count <= 8, guarantee tablebaseHit flag
            if (isTablebasePosition && !response.Info.TablebaseHit)
            {
                response = response with
                {
                    Info = response.Info with { TablebaseHit = true }
                };
            }

            // 5. Verify legality of the suggested move
            string verifiedMove = response.BestMove;
            bool isMoveLegal = CheckersGameRules.IsLegalMove(canonicalPdn, verifiedMove);

            if (!isMoveLegal)
            {
                _logger.LogWarning("Engine suggested illegal move '{Move}' for {Position}. Checking PV moves...", verifiedMove, canonicalPdn);
                bool foundLegalPv = false;
                foreach (var pvMove in response.Pv)
                {
                    if (CheckersGameRules.IsLegalMove(canonicalPdn, pvMove))
                    {
                        verifiedMove = pvMove;
                        foundLegalPv = true;
                        break;
                    }
                }

                if (!foundLegalPv)
                {
                    _logger.LogError("Neither bestMove nor any PV moves are legal for position {Position}", canonicalPdn);
                    return StatusCode(500, new { error = "Engine returned an illegal move and no legal alternative was found in PV." });
                }

                response = response with { BestMove = verifiedMove };
            }

            // 6. Save to cache for 15 minutes
            _cache.Set(cacheKey, response);

            sw.Stop();
            LogJsonMetrics(requestId, sw.ElapsedMilliseconds, response.Depth, response.Nodes, response.Info.TablebaseHit);

            return Ok(response);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Request {RequestId} exceeded hard timeout of {Timeout} ms.", requestId, hardTimeMs);
            return StatusCode(504, new { error = "Search timed out.", hardTimeMs });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing search request {RequestId}.", requestId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("v1/move/validate")]
    public IActionResult Validate([FromBody] ValidateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Position) || string.IsNullOrWhiteSpace(request.Move))
        {
            return StatusCode(422, new { error = "Position and Move cannot be empty." });
        }

        if (!_pdnService.TryValidateAndCanonicalize(request.Position, out string canonicalPdn, out _, out string? error))
        {
            return StatusCode(422, new { error = error ?? "Invalid PDN notation." });
        }

        // Validate move according to official checkers rules (mandatory jumps, kings, simple moves)
        bool isLegal = CheckersGameRules.IsLegalMove(canonicalPdn, request.Move);
        return Ok(new ValidateResponse(Legal: isLegal));
    }

    private void LogJsonMetrics(string requestId, long timeMs, int depth, long nodes, bool tablebaseHit)
    {
        var logPayload = new
        {
            requestId,
            timeMs,
            depth,
            nodes,
            tablebaseHit
        };

        _logger.LogInformation("{MetricsJson}", JsonSerializer.Serialize(logPayload));
    }
}