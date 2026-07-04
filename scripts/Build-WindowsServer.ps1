#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages the Raizen Security Server for Windows deployment
    (air-gapped compatible, no Docker required).

.DESCRIPTION
    Produces a self-contained deployment package at:
        release\RaizenServer-<version>\
            RaizenServer-Setup.exe    -- graphical setup wizard (run as Administrator)
            api\                      -- self-contained API binaries
            web\                      -- self-contained Web binaries
            agent\                    -- RaizenEndpoint.msi (if present in publish\installer\)
            postgres\                 -- drop a PostgreSQL Windows installer here for air-gapped install

    The setup wizard:
      * Checks / installs PostgreSQL (from postgres\ folder or pre-installed)
      * Generates RSA poll-signing key pair
      * Generates a self-signed TLS certificate for HTTPS
      * Installs the API and Admin Portal as Windows Services
      * Configures Windows Firewall rules

.PARAMETER Version
    Version string for the output folder name. Defaults to Directory.Build.props RaizenVersion.

.EXAMPLE
    .\scripts\Build-WindowsServer.ps1
    .\scripts\Build-WindowsServer.ps1 -Version "1.1.0"
#>
param(
    [string] $Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Log  { param($msg) Write-Host "[Raizen] $msg" -ForegroundColor Cyan }
function Fail { param($msg) Write-Error "[ERROR] $msg"; exit 1 }

$Root    = Split-Path $PSScriptRoot -Parent
$propsPath = Join-Path $Root "Directory.Build.props"
if ([string]::IsNullOrWhiteSpace($Version)) {
    if (!(Test-Path $propsPath)) { Fail "Version was not provided and Directory.Build.props was not found." }
    [xml] $props = Get-Content $propsPath
    $Version = $props.Project.PropertyGroup.RaizenVersion
    if ([string]::IsNullOrWhiteSpace($Version)) { Fail "RaizenVersion is missing from Directory.Build.props." }
}
$OutDir  = Join-Path $Root "release\RaizenServer-$Version"

Log "Building Raizen Security Server v$Version for Windows"
Log "Output: $OutDir"

# -- Clean output ---------------------------------------------------------------
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null
New-Item -ItemType Directory -Path "$OutDir\postgres" | Out-Null

# -- Publish API (self-contained, win-x64) -------------------------------------
Log "Publishing API (self-contained win-x64)..."
& dotnet publish "$Root\src\Raizen.Server\Raizen.Server.Api" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o "$OutDir\api" | Write-Host
if ($LASTEXITCODE -ne 0) { Fail "API publish failed." }

# -- Publish Web (self-contained, win-x64) -------------------------------------
Log "Publishing Web portal (self-contained win-x64)..."
& dotnet publish "$Root\src\Raizen.Server\Raizen.Server.Web" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -o "$OutDir\web" | Write-Host
if ($LASTEXITCODE -ne 0) { Fail "Web publish failed." }

# -- Publish Setup wizard (self-contained, win-x64) ----------------------------
Log "Publishing setup wizard..."
& dotnet publish "$Root\src\Raizen.Server\Raizen.Server.Setup" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$OutDir" | Write-Host
if ($LASTEXITCODE -ne 0) { Fail "Setup publish failed." }

# Rename to a friendly name
$setupExe = Get-ChildItem "$OutDir\RaizenServer-Setup*.exe" | Select-Object -First 1
if ($setupExe) {
    Rename-Item $setupExe.FullName "RaizenServer-Setup.exe" -ErrorAction SilentlyContinue
}

# -- Copy maintenance scripts --------------------------------------------------
Log "Copying server scripts..."
Copy-Item "$Root\scripts\Update-RaizenServer.ps1"    "$OutDir\Update-RaizenServer.ps1"
Copy-Item "$Root\scripts\Uninstall-RaizenServer.ps1" "$OutDir\Uninstall-RaizenServer.ps1"
Copy-Item "$Root\scripts\Reset-RaizenServer.ps1"     "$OutDir\Reset-RaizenServer.ps1"

# -- Build + copy agent MSI ----------------------------------------------------
Log "Building Endpoint MSI v$Version..."
& powershell -ExecutionPolicy Bypass -File "$Root\scripts\Build-EndpointMsi.ps1" -Version $Version
if ($LASTEXITCODE -ne 0) { Fail "Endpoint MSI build failed." }

$msiSource = "$Root\publish\installer\RaizenEndpoint.msi"
New-Item -ItemType Directory -Path "$OutDir\agent" -Force | Out-Null
Copy-Item $msiSource "$OutDir\agent\RaizenEndpoint.msi"
Log "Endpoint MSI copied to agent\"

# -- Copy agent uninstaller ----------------------------------------------------
Log "Copying endpoint uninstaller..."
Copy-Item "$Root\scripts\Uninstall-RaizenEndpoint.ps1" "$OutDir\agent\Uninstall-RaizenEndpoint.ps1"

# -- Write README --------------------------------------------------------------
$readme = @"
Raizen Security Server v$Version -- Windows Deployment Package
===============================================================

REQUIREMENTS
  - Windows Server 2019 / 2022 (or Windows 10/11 for testing)
  - Run as Administrator
  - PostgreSQL 15+ (installer can auto-install from the postgres\ folder)

HOW TO INSTALL
  1. (Air-gapped) Copy a PostgreSQL Windows installer
     (postgresql-*.exe from enterprisedb.com) into the postgres\ folder.

  2. Right-click RaizenServer-Setup.exe -> Run as administrator.

  3. Follow the wizard:
       Welcome  ->  Prerequisites  ->  Database  ->  Server Config  ->  Install  ->  Done

  4. On the Done screen, note your:
       - Admin portal URL  (https://<server>:5002)
       - TLS cert thumbprint  (needed when configuring endpoint agents)
       - Encryption key  (BACK THIS UP -- losing it is unrecoverable)

UPGRADING FROM A PREVIOUS VERSION
  Option A (recommended for existing installs):
    Right-click Update-RaizenServer.ps1 -> Run as administrator.
    This stops services, replaces binaries, restores your config, and
    restarts everything. DB schema changes apply automatically on startup.

  Option B (fresh install or first-time setup):
    Run RaizenServer-Setup.exe as Administrator.

ENDPOINT DEPLOYMENT
  Run agent\RaizenEndpoint.msi on each workstation as Administrator.
  The installer shows the EULA, creates firewall rules, and starts the service.

ENDPOINT UNINSTALL (start clean)
  Run as Administrator on the target workstation:
    PowerShell -ExecutionPolicy Bypass -File agent\Uninstall-RaizenEndpoint.ps1
  To keep the existing config/API key (reinstall scenario):
    PowerShell -ExecutionPolicy Bypass -File agent\Uninstall-RaizenEndpoint.ps1 -KeepConfig

SUPPORT
  Raizen Security -- support@raizensecurity.com
"@
Set-Content -Path "$OutDir\README.txt" -Value $readme -Encoding UTF8

# -- Summary -------------------------------------------------------------------
Log ""
Log "+----------------------------------------------------------+"
Log "  Package ready: release\RaizenServer-$Version\"
Log ""
Log "  RaizenServer-Setup.exe   -- setup wizard (first install)"
Log "  Update-RaizenServer.ps1  -- in-place upgrade script (existing installs)"
Log "  api\                     -- API service binaries"
Log "  web\                     -- Admin portal binaries"
Log "  agent\                   -- Endpoint MSI"
Log "  postgres\                -- Drop PostgreSQL installer here for air-gapped"
Log "+----------------------------------------------------------+"
