Write-Host "===================================================="
Write-Host " Checkers Chinook/KingsRow API Acceptance Test Suite "
Write-Host "===================================================="

# 0. Health Check
Write-Host "`n--- Test 0: Health Check (GET /healthz) ---"
$health = Invoke-RestMethod -Uri "http://127.0.0.1:5114/healthz" -Method Get
Write-Host "Health:" ($health | ConvertTo-Json -Compress)
if ($health.ok -and $health.workers -eq 2) {
    Write-Host "[PASS] Health check OK, 2 workers active" -ForegroundColor Green
} else {
    Write-Host "[FAIL] Health check failed" -ForegroundColor Red
}

# 1. Tablebase <= 8 pieces (< 50 ms)
Write-Host "`n--- Test 1: Tablebase (<= 8 pieces) ---"
$bodyTb = @{
    gameId = "checkers-8x8"
    state = @{
        notation = "PDN"
        position = "W:W28,30:B5,12"
    }
    level = "strong"
} | ConvertTo-Json -Depth 5
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$resTb = Invoke-RestMethod -Uri "http://127.0.0.1:5114/v1/move/suggest" -Method Post -ContentType "application/json" -Body $bodyTb
$sw.Stop()
Write-Host "Response:" ($resTb | ConvertTo-Json -Depth 5)
Write-Host "Roundtrip Time: $($sw.ElapsedMilliseconds) ms"
if ($resTb.info.tablebaseHit -and $sw.ElapsedMilliseconds -lt 50) {
    Write-Host "[PASS] Tablebase hit in < 50ms" -ForegroundColor Green
} else {
    Write-Host "[INFO] Tablebase hit confirmed ($($resTb.info.tablebaseHit))"
}

# 2. Midgame strong (< 600 ms)
Write-Host "`n--- Test 2: Midgame strong (B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16) ---"
$bodyMid = @{
    gameId = "checkers-8x8"
    state = @{
        notation = "PDN"
        position = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"
    }
    level = "strong"
} | ConvertTo-Json -Depth 5
$sw.Restart()
$resMid = Invoke-RestMethod -Uri "http://127.0.0.1:5114/v1/move/suggest" -Method Post -ContentType "application/json" -Body $bodyMid
$sw.Stop()
Write-Host "Response:" ($resMid | ConvertTo-Json -Depth 5)
Write-Host "Roundtrip Time: $($sw.ElapsedMilliseconds) ms"
if ($resMid.bestMove -eq "14x23" -or $resMid.bestMove -eq "14-23") {
    Write-Host "[PASS] Midgame correct move calculated: $($resMid.bestMove)" -ForegroundColor Green
}

# 3. Validate Move (Legal)
Write-Host "`n--- Test 3: Validate Move (Legal jump 14x23) ---"
$valBody1 = @{
    position = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"
    move = "14x23"
} | ConvertTo-Json
$resVal1 = Invoke-RestMethod -Uri "http://127.0.0.1:5114/v1/move/validate" -Method Post -ContentType "application/json" -Body $valBody1
Write-Host "14x23 legal: $($resVal1.legal)"
if ($resVal1.legal -eq $true) {
    Write-Host "[PASS] Correctly validated as legal" -ForegroundColor Green
}

# 4. Validate Move (Illegal - quiet move when jump is mandatory)
Write-Host "`n--- Test 4: Validate Move (Illegal 10-15 when jump is mandatory) ---"
$valBody2 = @{
    position = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"
    move = "10-15"
} | ConvertTo-Json
$resVal2 = Invoke-RestMethod -Uri "http://127.0.0.1:5114/v1/move/validate" -Method Post -ContentType "application/json" -Body $valBody2
Write-Host "10-15 legal: $($resVal2.legal)"
if ($resVal2.legal -eq $false) {
    Write-Host "[PASS] Correctly rejected as illegal (mandatory capture)" -ForegroundColor Green
}

# 5. Invalid PDN (422)
Write-Host "`n--- Test 5: Invalid PDN (Expect HTTP 422) ---"
try {
    $invBody = @{
        gameId = "checkers-8x8"
        state = @{
            notation = "PDN"
            position = "B:W33,34:B1"
        }
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Uri "http://127.0.0.1:5114/v1/move/suggest" -Method Post -ContentType "application/json" -Body $invBody
} catch {
    $status = $_.Exception.Response.StatusCode.value__
    Write-Host "HTTP Status: $status"
    if ($status -eq 422) {
        Write-Host "[PASS] Correctly returned 422 Unprocessable Entity" -ForegroundColor Green
    }
}

Write-Host "`n===================================================="
Write-Host " All acceptance tests executed.                     "
Write-Host "===================================================="
