#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the Raizen Server (API + Web services, files, firewall rules).
.PARAMETER DropDatabase
    Also drops the 'raizen' PostgreSQL database and role.
.PARAMETER PgSuperPass
    PostgreSQL superuser password (required when -DropDatabase is used).
.PARAMETER Force
    Skip confirmation prompt.
.EXAMPLE
    .\Uninstall-RaizenServer.ps1
    .\Uninstall-RaizenServer.ps1 -DropDatabase -PgSuperPass admin
#>
param(
    [switch]$DropDatabase,
    [string]$PgSuperPass = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$InstallRoot = "C:\Program Files\Raizen\Server"

function Write-Step { param([string]$msg) Write-Host "  $msg" -ForegroundColor Cyan }
function Write-OK   { param([string]$msg) Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn { param([string]$msg) Write-Host "  [WARN] $msg" -ForegroundColor Yellow }

Write-Host ""
Write-Host "  Raizen Server Uninstaller" -ForegroundColor White
Write-Host "  --------------------------" -ForegroundColor DarkGray
Write-Host ""

if (-not $Force) {
    $ans = Read-Host "  This will remove RaizenApi + RaizenWeb services and all installed files. Continue? [y/N]"
    if ($ans -notmatch '^[Yy]$') { Write-Host "  Aborted." ; exit 0 }
}

# -- Read cert thumbprint before we delete anything ----------------------
$certThumbprint = $null
$apiSettings = Join-Path $InstallRoot "Api\appsettings.Production.json"
if (Test-Path $apiSettings) {
    try {
        $s        = Get-Content $apiSettings -Raw | ConvertFrom-Json
        $certPath = $s.Kestrel.Endpoints.HttpsDefault.Certificate.Path
        $certPass = $s.Kestrel.Endpoints.HttpsDefault.Certificate.Password
        if (Test-Path $certPath) {
            $c = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certPath, $certPass)
            $certThumbprint = $c.Thumbprint
            $c.Dispose()
        }
    } catch { Write-Warn "Could not read cert thumbprint from config (will skip cert removal)." }
}

# -- Stop services -------------------------------------------------------
Write-Step "Stopping services..."
foreach ($svc in @("RaizenApi","RaizenWeb")) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.Status -eq "Running") {
            Stop-Service -Name $svc -Force
            Write-OK "Stopped $svc"
        } else {
            Write-Warn "$svc already stopped"
        }
    } else {
        Write-Warn "$svc not found (skipping)"
    }
}

# Give the OS a moment to release file handles after stopping services
Start-Sleep -Seconds 2

# -- Delete services -----------------------------------------------------
Write-Step "Removing services..."
foreach ($svc in @("RaizenApi","RaizenWeb")) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) {
        sc.exe delete $svc | Out-Null
        Write-OK "Deleted $svc"
    }
}

# -- Remove firewall rules -----------------------------------------------
Write-Step "Removing firewall rules..."
netsh advfirewall firewall delete rule name="Raizen API"        | Out-Null
netsh advfirewall firewall delete rule name="Raizen Web Portal" | Out-Null
Write-OK "Firewall rules removed"

# -- Remove TLS cert from machine stores ---------------------------------
Write-Step "Removing TLS certificates..."
if ($certThumbprint) {
    foreach ($storeName in @("My","Root")) {
        try {
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                $storeName, "LocalMachine")
            $store.Open("ReadWrite")
            $matches = $store.Certificates | Where-Object { $_.Thumbprint -eq $certThumbprint }
            foreach ($cert in $matches) {
                $store.Remove($cert)
                Write-OK "Removed cert $certThumbprint from LocalMachine\$storeName"
            }
            $store.Close()
        } catch {
            Write-Warn "Could not remove cert from LocalMachine\${storeName}: $_"
        }
    }
} else {
    Write-Warn "Cert thumbprint unknown -- skipping cert removal"
}

# -- Delete install directory --------------------------------------------
Write-Step "Deleting install files..."
if (Test-Path $InstallRoot) {
    # Use robocopy to force-delete locked files if Remove-Item fails
    try {
        Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop
        Write-OK "Deleted $InstallRoot"
    } catch {
        Write-Warn "Remove-Item failed, retrying with robocopy..."
        $empty = Join-Path $env:TEMP "raizen_empty_$(Get-Random)"
        New-Item -ItemType Directory -Path $empty | Out-Null
        robocopy $empty $InstallRoot /MIR /NFL /NDL /NJH /NJS | Out-Null
        Remove-Item $empty -Force
        Remove-Item $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $InstallRoot)) {
            Write-OK "Deleted $InstallRoot"
        } else {
            Write-Warn "Could not fully delete $InstallRoot -- remove manually"
        }
    }
} else {
    Write-Warn "$InstallRoot not found (already removed?)"
}

# -- Drop PostgreSQL database (optional) ---------------------------------
if ($DropDatabase) {
    Write-Step "Dropping PostgreSQL database and role..."

    if (-not $PgSuperPass) {
        $secure = Read-Host "  PostgreSQL superuser (postgres) password" -AsSecureString
        $PgSuperPass = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }

    $psql = Get-ChildItem "C:\Program Files\PostgreSQL" -Recurse -Filter "psql.exe" `
            -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $psql) {
        Write-Warn "psql.exe not found. Drop manually:"
        Write-Host "    DROP DATABASE IF EXISTS raizen;" -ForegroundColor DarkGray
        Write-Host "    DROP ROLE     IF EXISTS raizen;" -ForegroundColor DarkGray
    } else {
        $env:PGPASSWORD = $PgSuperPass
        & $psql.FullName -h localhost -U postgres -c "DROP DATABASE IF EXISTS raizen;" 2>&1 | Out-Null
        & $psql.FullName -h localhost -U postgres -c "DROP ROLE     IF EXISTS raizen;" 2>&1 | Out-Null
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Write-OK "Database 'raizen' and role 'raizen' dropped"
    }
}

Write-Host ""
Write-Host "  Uninstall complete." -ForegroundColor Green
Write-Host ""
