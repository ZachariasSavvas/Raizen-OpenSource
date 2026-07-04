#Requires -Version 5.1
<#
.SYNOPSIS
    Full end-to-end dev test: this machine acts as both Raizen Server and endpoint.
.DESCRIPTION
    Steps performed automatically:
      1. Create PostgreSQL role + database (raizen / raizen_dev_pass)
      2. Start Raizen.Server.Api  on http://localhost:5001  (new window)
      3. Start Raizen.Server.Web  on http://localhost:5000  (new window)
      4. Register this machine as an endpoint (API key generated here)
      5. Write raizen-config.json for the endpoint service
      6. Create a test CopyFile action in the catalog
      7. Start Raizen.Endpoint.Service  (new window)
      8. Start Raizen.Endpoint.Tray     (new window)
      9. Submit a test CopyFile request and approve it automatically
     10. Print a summary with the Web UI URL and test instructions

    REQUIREMENTS:
      - PostgreSQL running on localhost:5432
      - .NET 8 Desktop Runtime installed
      - Run from an elevated PowerShell for full elevation demos
#>
param(
    [string]$PgSuperUser = "postgres",
    [string]$PgSuperPass = "admin",
    [string]$ApiBase     = "http://localhost:5001",
    [string]$WebBase     = "http://localhost:5000",
    [string]$DevToken    = "dev-admin-token"
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$pubDir  = Join-Path $root "publish"
$apiDir  = Join-Path $pubDir "api"
$webDir  = Join-Path $pubDir "web"
$svcDir  = Join-Path $pubDir "service"
$trayDir = Join-Path $pubDir "tray"

function Write-Step([string]$msg) { Write-Host "" ; Write-Host ">>> $msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "    WARN $msg" -ForegroundColor Yellow }

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body, [hashtable]$Extra = @{})
    $headers = @{ "X-Raizen-Dev-Token" = $DevToken; "Content-Type" = "application/json" }
    foreach ($k in $Extra.Keys) { $headers[$k] = $Extra[$k] }
    $uri  = "$ApiBase$Path"
    $args = @{ Method = $Method; Uri = $uri; Headers = $headers; ErrorAction = "Stop" }
    if ($Body) { $args.Body = ($Body | ConvertTo-Json -Depth 10) }
    return Invoke-RestMethod @args
}

function Invoke-ApiEndpoint {
    param([string]$Method, [string]$Path, $Body, [string]$MachineId, [string]$ApiKey)
    $headers = @{
        "X-Raizen-MachineId" = $MachineId
        "X-Raizen-ApiKey"    = $ApiKey
        "Content-Type"       = "application/json"
    }
    $uri  = "$ApiBase$Path"
    $args = @{ Method = $Method; Uri = $uri; Headers = $headers; ErrorAction = "Stop" }
    if ($Body) { $args.Body = ($Body | ConvertTo-Json -Depth 10) }
    return Invoke-RestMethod @args
}

# ---- Step 1: PostgreSQL setup ------------------------------------------------
Write-Step "Setting up PostgreSQL (role: raizen, db: raizen)"

if ($PgSuperPass) { $env:PGPASSWORD = $PgSuperPass }

$psqlExe = Get-Command psql -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $psqlExe) {
    $candidates = @(
        "D:\PostgreSQL\18\bin\psql.exe",
        "C:\Program Files\PostgreSQL\17\bin\psql.exe",
        "C:\Program Files\PostgreSQL\16\bin\psql.exe",
        "C:\Program Files\PostgreSQL\15\bin\psql.exe",
        "C:\Program Files\PostgreSQL\14\bin\psql.exe"
    )
    $psqlExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ($psqlExe) {
    $createRole = "DO `$body`$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='raizen') THEN CREATE ROLE raizen LOGIN PASSWORD 'raizen_dev_pass'; END IF; END `$body`$;"
    & "$psqlExe" -h localhost -p 5432 -U $PgSuperUser -d postgres -c $createRole 2>&1 | ForEach-Object { "    [psql] $_" }

    $createDb = "SELECT 'CREATE DATABASE raizen OWNER raizen' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='raizen')\gexec"
    & "$psqlExe" -h localhost -p 5432 -U $PgSuperUser -d postgres -c $createDb 2>&1 | ForEach-Object { "    [psql] $_" }

    Write-OK "PostgreSQL role and database ready."
} else {
    Write-Warn "psql not found. Please create manually then press Enter:"
    Write-Warn "  CREATE ROLE raizen LOGIN PASSWORD 'raizen_dev_pass';"
    Write-Warn "  CREATE DATABASE raizen OWNER raizen;"
    Read-Host "Press Enter when ready"
}

# ---- Step 2: Start API -------------------------------------------------------
Write-Step "Starting Raizen API on $ApiBase"

$apiCmd = "`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:ASPNETCORE_URLS='$ApiBase'; Set-Location '$apiDir'; & '$apiDir\Raizen.Server.Api.exe'"
Start-Process powershell -ArgumentList "-NoProfile -NoExit -Command $apiCmd" -WindowStyle Normal

Write-Host "    Waiting for API to be ready..." -ForegroundColor Gray
$apiReady = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep 1
    try {
        $null = Invoke-WebRequest -Uri "$ApiBase/swagger/index.html" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        $apiReady = $true
        break
    } catch { }
    Write-Host "    [$i] waiting..." -ForegroundColor DarkGray
}
if (-not $apiReady) { throw "API did not start within 30 seconds. Check the API window for errors." }
Write-OK "API is responding at $ApiBase/swagger"

# ---- Step 3: Start Web -------------------------------------------------------
Write-Step "Starting Raizen Web UI on $WebBase"

$webCmd = "`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:ASPNETCORE_URLS='$WebBase'; Set-Location '$webDir'; & '$webDir\Raizen.Server.Web.exe'"
Start-Process powershell -ArgumentList "-NoProfile -NoExit -Command $webCmd" -WindowStyle Normal
Write-OK "Web UI window started. Browse to $WebBase after the script finishes."

# ---- Step 4: Register this machine -------------------------------------------
Write-Step "Registering this machine as a Raizen endpoint"

$machineId = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Cryptography" -Name MachineGuid).MachineGuid
Write-Host "    Machine GUID : $machineId" -ForegroundColor Gray

$keyBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
$apiKey = ([System.BitConverter]::ToString($keyBytes) -replace '-','').ToLower()
Write-Host "    New API key  : $($apiKey.Substring(0,12))..." -ForegroundColor Gray

$regBody = @{
    MachineName  = $env:COMPUTERNAME
    OsVersion    = [System.Environment]::OSVersion.VersionString
    AgentVersion = "1.0.0-dev"
}
$regExtra = @{
    "X-Raizen-MachineId" = $machineId
    "X-Raizen-NewApiKey" = $apiKey
}
$registration = Invoke-Api -Method POST -Path "/api/v1/endpoints/register" -Body $regBody -Extra $regExtra
Write-OK "Registered. Registration ID: $($registration.id)"

# ---- Step 5: Write raizen-config.json ----------------------------------------
Write-Step "Writing raizen-config.json for the endpoint service"

$configPath = Join-Path $svcDir "raizen-config.json"
[ordered]@{
    ServerUrl                = $ApiBase
    ApiKey                   = $apiKey
    MachineId                = $machineId
    PollIntervalSeconds      = 5
    HeartbeatIntervalSeconds = 30
    TlsPinThumbprint         = $null
} | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
Write-OK "Written to $configPath"

# ---- Step 6: Create test action in catalog -----------------------------------
Write-Step "Creating test action: CopyFile"

$existingActions = Invoke-Api -Method GET -Path "/api/v1/actions/all"
$testAction = $existingActions | Where-Object { $_.displayName -eq "Test: Copy File" } | Select-Object -First 1

if (-not $testAction) {
    $actionBody = @{
        DisplayName           = "Test: Copy File"
        Description           = "DEV TEST - copies SourcePath to DestinationPath. Safe, non-destructive."
        ActionType            = 40
        AutoApprove           = $false
        ApprovalWindowMinutes = 30
        IsEnabled             = $true
        Parameters            = @(
            @{ Key = "SourcePath";      DisplayName = "Source Path";      Description = "Absolute path of the source file";      Type = 3; Required = $true; ValidationPattern = $null; DefaultValue = $null },
            @{ Key = "DestinationPath"; DisplayName = "Destination Path"; Description = "Absolute path of the destination file"; Type = 3; Required = $true; ValidationPattern = $null; DefaultValue = $null }
        )
        ApproverGroupIds      = @()
    }
    $testAction = Invoke-Api -Method POST -Path "/api/v1/actions" -Body $actionBody
    Write-OK "Created action. ID: $($testAction.id)"
} else {
    Write-OK "Test action already exists. ID: $($testAction.id)"
}
$actionId = $testAction.id

# ---- Step 7: Start Endpoint Service ------------------------------------------
Write-Step "Starting Raizen Endpoint Service"

$svcCmd = "`$env:RAIZEN_CONFIG_PATH='$configPath'; Set-Location '$svcDir'; & '$svcDir\Raizen.Endpoint.Service.exe'"
Start-Process powershell -ArgumentList "-NoProfile -NoExit -Command $svcCmd" -WindowStyle Normal
Write-OK "Service window started. Waiting 5 seconds for first poll..."
Start-Sleep 5

# ---- Step 8: Start Tray App --------------------------------------------------
Write-Step "Starting Raizen Endpoint Tray"

$trayCmd = "`$env:RAIZEN_CONFIG_PATH='$configPath'; Set-Location '$trayDir'; & '$trayDir\Raizen.Endpoint.Tray.exe'"
Start-Process powershell -ArgumentList "-NoProfile -NoExit -Command $trayCmd" -WindowStyle Minimized
Write-OK "Tray app started (check the system tray)."

# ---- Step 9: Submit a test CopyFile request ----------------------------------
Write-Step "Submitting a test CopyFile elevation request"

$null = New-Item -ItemType Directory -Path "C:\Temp" -Force
$sourceFile = "C:\Temp\raizen-test-source.txt"
$destFile   = "C:\Temp\raizen-test-dest.txt"
"Raizen elevation test $(Get-Date)" | Set-Content -Path $sourceFile -Encoding UTF8
if (Test-Path $destFile) { Remove-Item $destFile -Force }

$submitBody = @{
    ActionId      = $actionId
    Justification = "Automated dev test - verifying full elevation flow end-to-end."
    Parameters    = @{
        SourcePath      = $sourceFile
        DestinationPath = $destFile
        "__requester_upn"          = "dev-tester@raizen.local"
        "__requester_display_name" = "Dev Tester"
    }
}
$request = Invoke-ApiEndpoint -Method POST -Path "/api/v1/requests" -Body $submitBody -MachineId $machineId -ApiKey $apiKey
Write-OK "Request submitted. ID: $($request.id)  Status: $($request.status)"

# ---- Step 10: Approve the request --------------------------------------------
Write-Step "Approving the request via API (dev token)"

$reviewBody = @{ Approved = $true; Note = "Auto-approved by dev test script." }
$approved = Invoke-Api -Method POST -Path "/api/v1/requests/$($request.id)/review" -Body $reviewBody
Write-OK "Approved. Status: $($approved.status)"

# ---- Step 11: Wait for execution ---------------------------------------------
Write-Step "Waiting for endpoint service to execute the request..."

$final = $null
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep 2
    $final = Invoke-Api -Method GET -Path "/api/v1/requests/$($request.id)"
    Write-Host "    [$i] Status: $($final.status)" -ForegroundColor Gray
    if ($final.status -in @("Succeeded", "Failed", "Cancelled")) { break }
}

Write-Host ""
if ($final.status -eq "Succeeded") {
    Write-Host "=== TEST PASSED ===" -ForegroundColor Green
    Write-Host "    Result   : $($final.executionResult)" -ForegroundColor Green
    if (Test-Path $destFile) {
        Write-Host "    Dest file: $destFile" -ForegroundColor Green
        Write-Host "    Content  : $(Get-Content $destFile)" -ForegroundColor Green
    }
} elseif ($final.status -eq "Failed") {
    Write-Host "=== TEST FAILED ===" -ForegroundColor Red
    Write-Host "    Error: $($final.executionError)" -ForegroundColor Red
} else {
    Write-Host "=== TEST INCONCLUSIVE - final status: $($final.status) ===" -ForegroundColor Yellow
    Write-Host "    Check the service window for logs." -ForegroundColor Yellow
}

# ---- Summary -----------------------------------------------------------------
Write-Host ""
Write-Host "---------------------------------------------------------------------" -ForegroundColor White
Write-Host " Raizen Dev Environment is running" -ForegroundColor White
Write-Host "---------------------------------------------------------------------" -ForegroundColor White
Write-Host " Admin Web UI  : $WebBase              (auto-logged in as dev-admin@raizen.local)" -ForegroundColor Cyan
Write-Host " API Swagger   : $ApiBase/swagger      (X-Raizen-Dev-Token: $DevToken)" -ForegroundColor Cyan
Write-Host " Machine ID    : $machineId" -ForegroundColor Gray
Write-Host " Config file   : $configPath" -ForegroundColor Gray
Write-Host ""
Write-Host " Use the Tray app (system tray icon) to submit more requests." -ForegroundColor White
Write-Host " Approve them in the Web UI at $WebBase/requests" -ForegroundColor White
Write-Host "---------------------------------------------------------------------" -ForegroundColor White
