#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the Raizen API and Web server for local testing.
.DESCRIPTION
    - Starts Raizen.Server.Api  on http://localhost:5001
    - Starts Raizen.Server.Web  on http://localhost:5000
    - Both use appsettings.Development.json (DevMode token, local PostgreSQL)

    Prerequisites:
      1. PostgreSQL running locally on port 5432
         CREATE ROLE raizen LOGIN PASSWORD 'raizen_dev_pass';
         CREATE DATABASE raizen OWNER raizen;
      2. .NET 8 Runtime installed

    Dev API token for testing (pass as header X-Raizen-Dev-Token):
        dev-admin-token

    Press Ctrl+C to stop both servers.
#>

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "Starting Raizen API  → http://localhost:5001" -ForegroundColor Cyan
Write-Host "Starting Raizen Web  → http://localhost:5000" -ForegroundColor Cyan
Write-Host ""
Write-Host "API dev token: dev-admin-token  (header: X-Raizen-Dev-Token)" -ForegroundColor Yellow
Write-Host ""

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS        = 'http://localhost:5001'

$apiJob = Start-Job -ScriptBlock {
    param($dir)
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS        = 'http://localhost:5001'
    Set-Location $dir
    & "$dir\Raizen.Server.Api.exe"
} -ArgumentList "$root\publish\api"

$env:ASPNETCORE_URLS = 'http://localhost:5000'

$webJob = Start-Job -ScriptBlock {
    param($dir)
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS        = 'http://localhost:5000'
    Set-Location $dir
    & "$dir\Raizen.Server.Web.exe"
} -ArgumentList "$root\publish\web"

Write-Host "Servers started (Job IDs: API=$($apiJob.Id), Web=$($webJob.Id))" -ForegroundColor Green
Write-Host "Streaming output... (Ctrl+C to stop)" -ForegroundColor Gray
Write-Host ""

try {
    while ($true) {
        Receive-Job -Job $apiJob -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "[API] $_" -ForegroundColor DarkCyan }
        Receive-Job -Job $webJob -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "[WEB] $_" -ForegroundColor DarkGreen }
        Start-Sleep -Milliseconds 500
    }
}
finally {
    Write-Host "`nStopping servers..." -ForegroundColor Yellow
    Stop-Job  -Job $apiJob, $webJob
    Remove-Job -Job $apiJob, $webJob -Force
}
