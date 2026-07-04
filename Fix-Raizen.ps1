# Fix-Raizen.ps1 - Diagnose and repair a 401 / unreachable server situation
# Run this whenever the tray app gets a 401 or "server unreachable" error.

$ConfigPath = 'C:\ProgramData\Raizen\raizen-config.json'
$ApiExe     = 'C:\Users\user\source\repos\Raizen\publish\api\Raizen.Server.Api.exe'
$WebExe     = 'C:\Users\user\source\repos\Raizen\publish\web\Raizen.Server.Web.exe'
$ApiDir     = Split-Path $ApiExe
$WebDir     = Split-Path $WebExe

function Write-Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "   OK   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "   FAIL $msg" -ForegroundColor Red }
function Write-Warn($msg) { Write-Host "   WARN $msg" -ForegroundColor Yellow }

# -- 1. Read config -----------------------------------------------------------
Write-Step "Reading endpoint config"
if (-not (Test-Path $ConfigPath)) {
    Write-Fail "Config not found at $ConfigPath"
    exit 1
}
$cfg       = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$ServerUrl = $cfg.ServerUrl.TrimEnd('/')
$MachineId = $cfg.MachineId
$ApiKey    = $cfg.ApiKey
Write-Ok "ServerUrl = $ServerUrl"
Write-Ok "MachineId = $MachineId"
Write-Ok "ApiKey    = $($ApiKey.Substring(0,8))..."

$headers = @{
    'X-Raizen-MachineId' = $MachineId
    'X-Raizen-ApiKey'    = $ApiKey
}

# -- 2. Check processes -------------------------------------------------------
Write-Step "Checking server processes"
$apiProc = Get-Process -Name 'Raizen.Server.Api' -ErrorAction SilentlyContinue
$webProc = Get-Process -Name 'Raizen.Server.Web' -ErrorAction SilentlyContinue

if ($apiProc) { Write-Ok "API running (PID $($apiProc.Id))" }
else          { Write-Warn "API NOT running" }

if ($webProc) { Write-Ok "Web running (PID $($webProc.Id))" }
else          { Write-Warn "Web NOT running" }

# -- 3. Test API auth ---------------------------------------------------------
Write-Step "Testing API authentication ($ServerUrl/api/v1/actions)"
$authOk = $false
try {
    $resp = Invoke-WebRequest -Uri "$ServerUrl/api/v1/actions" -Headers $headers -TimeoutSec 5 -ErrorAction Stop
    $count = ($resp.Content | ConvertFrom-Json).Count
    Write-Ok "HTTP $($resp.StatusCode) - auth OK, $count actions available"
    $authOk = $true
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 401) {
        Write-Fail "HTTP 401 - machine/key mismatch or machine not registered in DB"
    } elseif ($null -eq $code) {
        Write-Fail "Connection refused - server not reachable at $ServerUrl"
    } else {
        Write-Fail "HTTP $code - $($_.Exception.Message)"
    }
}

# -- 4. Restart if needed -----------------------------------------------------
if (-not $authOk -or -not $apiProc -or -not $webProc) {
    Write-Step "Restarting server processes"
    Get-Process | Where-Object { $_.Name -in 'Raizen.Server.Api','Raizen.Server.Web' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 1

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS        = 'http://localhost:5001'
    Start-Process -FilePath $ApiExe -WorkingDirectory $ApiDir -WindowStyle Minimized

    $env:ASPNETCORE_URLS = 'http://localhost:5002'
    Start-Process -FilePath $WebExe -WorkingDirectory $WebDir -WindowStyle Minimized

    Write-Host "   Waiting for startup..." -ForegroundColor Yellow
    Start-Sleep 5

    # Re-test after restart
    try {
        $resp = Invoke-WebRequest -Uri "$ServerUrl/api/v1/actions" -Headers $headers -TimeoutSec 5 -ErrorAction Stop
        Write-Ok "HTTP $($resp.StatusCode) - auth OK after restart"
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -eq 401) {
            Write-Fail "Still 401 after restart - API key in config does not match DB hash"
            Write-Warn "To fix: go to the Web portal, delete and re-register this machine, then update $ConfigPath with the new key"
        } else {
            Write-Fail "Still failing after restart - $($_.Exception.Message)"
        }
        exit 1
    }
} else {
    Write-Ok "Everything healthy - no restart needed"
}

# -- 5. Summary ---------------------------------------------------------------
Write-Step "Final process status"
Get-Process | Where-Object { $_.Name -in 'Raizen.Server.Api','Raizen.Server.Web' } |
    Select-Object Id, Name | Format-Table -AutoSize

Write-Host "`nDone. Retry your request in the tray app.`n" -ForegroundColor Green
