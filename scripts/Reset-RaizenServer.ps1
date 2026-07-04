#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Completely resets the Raizen Server installation on this machine.
    Stops services, drops the database, and removes all installed files.
    Run this before re-running RaizenServer-Setup.exe for a clean install.

.PARAMETER PgPass
    PostgreSQL superuser (postgres) password. Required to drop the database.

.PARAMETER PgHost
    PostgreSQL host. Default: localhost

.PARAMETER PgPort
    PostgreSQL port. Default: 5432

.EXAMPLE
    .\Reset-RaizenServer.ps1 -PgPass pandoras
#>
param(
    [Parameter(Mandatory)]
    [string]$PgPass,

    [string]$PgHost = "localhost",
    [int]   $PgPort = 5432
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$InstallRoot = "C:\Program Files\Raizen\Server"

function Step { param($msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok   { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Warn { param($msg) Write-Host "    [--] $msg" -ForegroundColor Yellow }
function Fail { param($msg) Write-Host "    [!!] $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "Raizen Server -- Full Reset" -ForegroundColor White
Write-Host "===========================" -ForegroundColor White

# ── 1. Stop + remove services ─────────────────────────────────────────────────
Step "Stopping Raizen services..."
foreach ($svc in @("RaizenApi", "RaizenWeb")) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) {
        Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
        & sc.exe delete $svc | Out-Null
        Ok "Service '$svc' stopped and removed."
    } else {
        Warn "Service '$svc' not found (already removed)."
    }
}
Start-Sleep -Seconds 2   # let OS release file handles

# ── 2. Remove firewall rules ──────────────────────────────────────────────────
Step "Removing firewall rules..."
netsh advfirewall firewall delete rule name="Raizen API"        | Out-Null
netsh advfirewall firewall delete rule name="Raizen Web Portal" | Out-Null
Ok "Firewall rules removed."

# ── 3. Remove TLS cert (read thumbprint from config before deleting files) ────
Step "Removing TLS certificate..."
$certThumbprint = $null
$apiSettings = Join-Path $InstallRoot "Api\appsettings.Production.json"
if (Test-Path $apiSettings) {
    try {
        $s        = Get-Content $apiSettings -Raw | ConvertFrom-Json
        $certPath = $s.Kestrel.Endpoints.HttpsDefault.Certificate.Path
        $certPass = $s.Kestrel.Endpoints.HttpsDefault.Certificate.Password
        if ($certPath -and (Test-Path $certPath)) {
            $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certPath, $certPass)
            $certThumbprint = $cert.Thumbprint
            $cert.Dispose()
        }
    } catch { Warn "Could not read cert thumbprint from config." }
}
if ($certThumbprint) {
    foreach ($storeName in @("My", "Root")) {
        try {
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, "LocalMachine")
            $store.Open("ReadWrite")
            $store.Certificates | Where-Object { $_.Thumbprint -eq $certThumbprint } |
                ForEach-Object { $store.Remove($_) }
            $store.Close()
            Ok "Removed cert $certThumbprint from LocalMachine\$storeName"
        } catch { Warn "Could not remove cert from LocalMachine\${storeName}: $_" }
    }
} else {
    Warn "No cert thumbprint found — skipping cert removal."
}

# ── 4. Delete install files ───────────────────────────────────────────────────
Step "Deleting installed files ($InstallRoot)..."
if (Test-Path $InstallRoot) {
    try {
        Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop
        Ok "Install directory removed."
    } catch {
        Warn "Remove-Item failed, trying robocopy mirror..."
        $empty = Join-Path $env:TEMP "raizen_empty_$(Get-Random)"
        New-Item -ItemType Directory -Path $empty | Out-Null
        robocopy $empty $InstallRoot /MIR /NFL /NDL /NJH /NJS | Out-Null
        Remove-Item $empty -Force -ErrorAction SilentlyContinue
        Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $InstallRoot) { Fail "Could not fully delete $InstallRoot — remove manually." }
        else { Ok "Install directory removed." }
    }
} else {
    Warn "Install directory not found (already removed)."
}

# ── 5. Drop PostgreSQL database and role ──────────────────────────────────────
Step "Dropping PostgreSQL database 'raizen' and role 'raizen'..."

# Find psql.exe — check common install paths
$psql = Get-ChildItem "C:\Program Files\PostgreSQL" -Recurse -Filter "psql.exe" `
        -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $psql) {
    $psql = Get-ChildItem "D:\PostgreSQL" -Recurse -Filter "psql.exe" `
            -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $psql) {
    Fail "psql.exe not found. Drop the database manually:"
    Write-Host "    DROP DATABASE IF EXISTS raizen;" -ForegroundColor DarkGray
    Write-Host "    DROP ROLE     IF EXISTS raizen;" -ForegroundColor DarkGray
} else {
    $env:PGPASSWORD = $PgPass
    $dropDb   = & $psql.FullName -h $PgHost -p $PgPort -U postgres -c "DROP DATABASE IF EXISTS raizen;" 2>&1
    $dropRole = & $psql.FullName -h $PgHost -p $PgPort -U postgres -c "DROP ROLE IF EXISTS raizen;" 2>&1
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue

    if ($LASTEXITCODE -eq 0) {
        Ok "Database 'raizen' dropped."
        Ok "Role 'raizen' dropped."
    } else {
        Fail "psql errors:"
        Write-Host $dropDb   -ForegroundColor DarkGray
        Write-Host $dropRole -ForegroundColor DarkGray
    }
}

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "===========================" -ForegroundColor White
Write-Host "Reset complete. Run RaizenServer-Setup.exe to reinstall." -ForegroundColor Green
Write-Host ""
