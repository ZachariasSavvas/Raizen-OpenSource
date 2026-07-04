#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Hot-updates the Raizen Endpoint service and tray app from the latest publish output.
    Run this after every build to push new binaries without reinstalling the MSI.
#>
[CmdletBinding()] param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ServiceSrc = Join-Path $RepoRoot 'publish\service'
$TraySrc    = Join-Path $RepoRoot 'publish\tray'
$ServiceDst = 'C:\Program Files\Raizen\Service'
$TrayDst    = 'C:\Program Files\Raizen\Tray'

Write-Host '=== Raizen Endpoint Updater ===' -ForegroundColor Cyan

# ── Stop service ─────────────────────────────────────────────────────────────
Write-Host 'Stopping RaizenEndpoint service...' -ForegroundColor Yellow
Stop-Service RaizenEndpoint -Force
Start-Sleep -Seconds 2

# ── Kill tray if running ──────────────────────────────────────────────────────
$trayProcs = Get-Process -Name 'Raizen.Endpoint.Tray' -ErrorAction SilentlyContinue
if ($trayProcs) {
    Write-Host 'Stopping Raizen tray app...' -ForegroundColor Yellow
    $trayProcs | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# ── Copy service files (skip raizen-config.json — keep installed config) ─────
Write-Host 'Copying service binaries...' -ForegroundColor Gray
Copy-Item -Force "$ServiceSrc\Raizen.Endpoint.Service.exe" "$ServiceDst\"
Copy-Item -Force "$ServiceSrc\appsettings.json"            "$ServiceDst\"
if (Test-Path "$ServiceSrc\Raizen.Endpoint.Service.pdb") {
    Copy-Item -Force "$ServiceSrc\Raizen.Endpoint.Service.pdb" "$ServiceDst\"
}

# ── Copy tray files (only Raizen-specific, leave Windows runtime alone) ───────
Write-Host 'Copying tray binaries...' -ForegroundColor Gray
$trayFiles = @(
    'Raizen.Endpoint.Tray.exe',
    'Raizen.Endpoint.Tray.dll',
    'Raizen.Endpoint.Tray.deps.json',
    'Raizen.Endpoint.Tray.runtimeconfig.json',
    'Raizen.Endpoint.Shared.dll',
    'Raizen.Shared.dll',
    'Raizen.ico'
)
foreach ($f in $trayFiles) {
    $src = Join-Path $TraySrc $f
    if (Test-Path $src) {
        Copy-Item -Force $src "$TrayDst\"
    }
}

# ── Start service ─────────────────────────────────────────────────────────────
Write-Host 'Starting RaizenEndpoint service...' -ForegroundColor Yellow
Start-Service RaizenEndpoint
Start-Sleep -Seconds 2

$svc = Get-Service RaizenEndpoint
if ($svc.Status -eq 'Running') {
    Write-Host "Service is Running." -ForegroundColor Green
} else {
    Write-Host "Service status: $($svc.Status)" -ForegroundColor Red
}

# ── Restart tray for current user ────────────────────────────────────────────
$trayExe = "$TrayDst\Raizen.Endpoint.Tray.exe"
$config  = 'C:\ProgramData\Raizen\raizen-config.json'
Write-Host 'Launching tray app...' -ForegroundColor Gray
Start-Process -FilePath $trayExe -ArgumentList "--config=`"$config`""

Write-Host 'Done.' -ForegroundColor Green
