#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Updates an existing Raizen Server installation in-place.

.DESCRIPTION
    Stops the RaizenApi and RaizenWeb services, replaces the binaries with the
    new versions from the package's api\ and web\ folders, then restarts the
    services. Existing configuration files (appsettings.Production.json) and
    the TLS certificate are preserved. DB schema changes are applied
    automatically on first startup via the startup migration blocks in Program.cs.

.PARAMETER PackageDir
    Root folder of the extracted release package (the folder that contains
    api\, web\, and this script). Defaults to the script's own directory.

.PARAMETER AgentVersion
    When specified, updates AgentDeployment:CurrentVersion in the API's
    appsettings.Production.json to this value so endpoints will self-update.
    Should match the version of agent\RaizenEndpoint.msi in this package.

.PARAMETER Force
    Skip the confirmation prompt.

.EXAMPLE
    # Run from the extracted release folder:
    .\Update-RaizenServer.ps1

    # Specify package path and bump agent version in one step:
    .\Update-RaizenServer.ps1 -PackageDir "C:\Temp\RaizenServer-1.1.0" -AgentVersion "1.1.0" -Force
#>
param(
    [string] $PackageDir    = $PSScriptRoot,
    [string] $AgentVersion  = '',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$InstallRoot = 'C:\Program Files\Raizen\Server'
$ApiInstall  = Join-Path $InstallRoot 'Api'
$WebInstall  = Join-Path $InstallRoot 'Web'
$ApiSource   = Join-Path $PackageDir  'api'
$WebSource   = Join-Path $PackageDir  'web'
$AgentMsiSource = Join-Path $PackageDir 'agent\RaizenEndpoint.msi'

function Write-Step { param([string]$m) Write-Host "  $m" -ForegroundColor Cyan }
function Write-OK   { param([string]$m) Write-Host "  [OK] $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Fail       { param([string]$m) Write-Host "`n  [ERROR] $m" -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host '  Raizen Server Update' -ForegroundColor White
Write-Host '  --------------------' -ForegroundColor DarkGray
Write-Host ''

# -- Validate package --------------------------------------------------------
if (-not (Test-Path $ApiSource)) { Fail "api\ not found in package at: $ApiSource" }
if (-not (Test-Path $WebSource)) { Fail "web\ not found in package at: $WebSource" }

# -- Check existing install --------------------------------------------------
$hasApi = Test-Path $ApiInstall
$hasWeb = Test-Path $WebInstall
if (-not $hasApi -and -not $hasWeb) {
    Fail "No existing Raizen Server installation found at $InstallRoot. Run RaizenServer-Setup.exe instead."
}

# -- Confirm -----------------------------------------------------------------
if (-not $Force) {
    Write-Host "  This will update the Raizen Server binaries at:" -ForegroundColor Yellow
    Write-Host "    API : $ApiInstall" -ForegroundColor DarkGray
    Write-Host "    Web : $WebInstall" -ForegroundColor DarkGray
    Write-Host "  Existing configuration and certificates will be preserved." -ForegroundColor DarkGray
    Write-Host ''
    $ans = Read-Host '  Continue? [y/N]'
    if ($ans -notmatch '^[Yy]$') { Write-Host '  Aborted.'; exit 0 }
}

# -- Backup appsettings.Production.json -------------------------------------
Write-Step 'Backing up configuration files...'
$tmpDir    = Join-Path $env:TEMP "raizen-update-$(Get-Random)"
$apiTmpCfg = Join-Path $tmpDir 'api-appsettings.Production.json'
$webTmpCfg = Join-Path $tmpDir 'web-appsettings.Production.json'
New-Item -ItemType Directory -Path $tmpDir | Out-Null

$apiCfg = Join-Path $ApiInstall 'appsettings.Production.json'
$webCfg = Join-Path $WebInstall 'appsettings.Production.json'

if (Test-Path $apiCfg) { Copy-Item $apiCfg $apiTmpCfg; Write-OK "Backed up API config" }
else                   { Write-Warn "API appsettings.Production.json not found (will not restore)" }

if (Test-Path $webCfg) { Copy-Item $webCfg $webTmpCfg; Write-OK "Backed up Web config" }
else                   { Write-Warn "Web appsettings.Production.json not found (will not restore)" }

# -- Stop services -----------------------------------------------------------
Write-Step 'Stopping services...'
foreach ($svc in @('RaizenApi', 'RaizenWeb')) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.Status -eq 'Running') {
            Stop-Service -Name $svc -Force
            # Wait up to 30 s for the process to release file handles
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Service -Name $svc).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
            }
            Write-OK "Stopped $svc"
        } else {
            Write-OK "$svc already stopped"
        }
    } else {
        Write-Warn "$svc service not found (skipping)"
    }
}
Start-Sleep -Seconds 1   # give OS a moment to release file locks

# -- Copy new binaries -------------------------------------------------------
Write-Step 'Copying new API binaries...'
if ($hasApi) {
    New-Item -ItemType Directory -Path $ApiInstall -Force | Out-Null
    # /MIR mirrors the source; /XF excludes Production config and license files
    & robocopy $ApiSource $ApiInstall /MIR /NFL /NDL /NJH /NJS `
        /XF 'appsettings.Production.json' '*.lic' | Out-Null
    Write-OK "API binaries updated"
}

Write-Step 'Copying new Web binaries...'
if ($hasWeb) {
    New-Item -ItemType Directory -Path $WebInstall -Force | Out-Null
    & robocopy $WebSource $WebInstall /MIR /NFL /NDL /NJH /NJS `
        /XF 'appsettings.Production.json' '*.lic' | Out-Null
    Write-OK "Web binaries updated"
}

# -- Restore appsettings.Production.json ------------------------------------
Write-Step 'Restoring configuration files...'
if (Test-Path $apiTmpCfg) { Copy-Item $apiTmpCfg $apiCfg -Force; Write-OK "Restored API config" }
if (Test-Path $webTmpCfg) { Copy-Item $webTmpCfg $webCfg -Force; Write-OK "Restored Web config" }
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue

# -- Copy endpoint MSI for agent auto-update --------------------------------
if (Test-Path $AgentMsiSource) {
    Write-Step 'Copying endpoint MSI for agent auto-update...'

    $installerPath = ''
    if (Test-Path $apiCfg) {
        try {
            $cfg = Get-Content $apiCfg -Raw | ConvertFrom-Json
            if ($null -ne $cfg.AgentDeployment -and
                $cfg.AgentDeployment.PSObject.Properties.Name -contains 'InstallerPath') {
                $installerPath = [string] $cfg.AgentDeployment.InstallerPath
            }
        } catch {
            Write-Warn "Could not read AgentDeployment:InstallerPath from API config: $($_.Exception.Message)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($installerPath)) {
        $installerPath = Join-Path $InstallRoot 'updates\RaizenEndpoint.msi'
        if (Test-Path $apiCfg) {
            $cfg = Get-Content $apiCfg -Raw | ConvertFrom-Json
            if ($null -eq $cfg.AgentDeployment) {
                $cfg | Add-Member -NotePropertyName AgentDeployment -NotePropertyValue ([PSCustomObject]@{})
            }
            if ($cfg.AgentDeployment.PSObject.Properties.Name -notcontains 'InstallerPath') {
                $cfg.AgentDeployment | Add-Member -NotePropertyName InstallerPath -NotePropertyValue $installerPath
            } else {
                $cfg.AgentDeployment.InstallerPath = $installerPath
            }
            $cfg | ConvertTo-Json -Depth 10 | Set-Content $apiCfg -Encoding UTF8
            Write-OK "AgentDeployment:InstallerPath set to $installerPath"
        } else {
            Write-Warn "API config not found -- copied MSI to default path, but InstallerPath was not saved"
        }
    }

    $installerDir = Split-Path $installerPath -Parent
    New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
    Copy-Item $AgentMsiSource $installerPath -Force
    Write-OK "Endpoint MSI copied to $installerPath"
} else {
    Write-Warn "Endpoint MSI not found in package at $AgentMsiSource -- agent auto-update package was not refreshed"
}

# -- Start services ----------------------------------------------------------
Write-Step 'Starting services...'
foreach ($svc in @('RaizenApi', 'RaizenWeb')) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) {
        Start-Service -Name $svc
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Service -Name $svc).Status -ne 'Running' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        if ((Get-Service -Name $svc).Status -eq 'Running') {
            Write-OK "$svc started"
        } else {
            Write-Warn "$svc did not reach Running state within 60 s -- check the Windows Event Log"
        }
    } else {
        Write-Warn "$svc not found -- re-run RaizenServer-Setup.exe to register services"
    }
}

# -- Bump AgentDeployment:CurrentVersion ------------------------------------
if ($AgentVersion -ne '') {
    Write-Step "Updating AgentDeployment:CurrentVersion to $AgentVersion..."
    if (Test-Path $apiCfg) {
        $cfg = Get-Content $apiCfg -Raw | ConvertFrom-Json
        if ($null -eq $cfg.AgentDeployment) {
            $cfg | Add-Member -NotePropertyName AgentDeployment -NotePropertyValue ([PSCustomObject]@{ CurrentVersion = $AgentVersion })
        } elseif ($cfg.AgentDeployment.PSObject.Properties.Name -notcontains 'CurrentVersion') {
            $cfg.AgentDeployment | Add-Member -NotePropertyName CurrentVersion -NotePropertyValue $AgentVersion
        } else {
            $cfg.AgentDeployment.CurrentVersion = $AgentVersion
        }
        $cfg | ConvertTo-Json -Depth 10 | Set-Content $apiCfg -Encoding UTF8
        Write-OK "AgentDeployment:CurrentVersion set to $AgentVersion"

        # Restart API so it picks up the new version immediately
        Write-Step 'Restarting RaizenApi to apply version change...'
        Restart-Service RaizenApi
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Service RaizenApi).Status -ne 'Running' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        if ((Get-Service RaizenApi).Status -eq 'Running') {
            Write-OK 'RaizenApi restarted'
        } else {
            Write-Warn 'RaizenApi did not reach Running state within 60 s -- check the Windows Event Log'
        }
    } else {
        Write-Warn "API appsettings.Production.json not found -- skipping agent version bump"
    }
}

Write-Host ''
Write-Host '  Update complete.' -ForegroundColor Green
Write-Host ''
Write-Host '  DB schema changes (new columns, tables) are applied automatically' -ForegroundColor DarkGray
Write-Host '  on first startup -- no manual SQL required.' -ForegroundColor DarkGray
Write-Host ''
