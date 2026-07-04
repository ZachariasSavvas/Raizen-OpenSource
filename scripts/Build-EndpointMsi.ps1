#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages the Raizen Endpoint agent MSI.

.DESCRIPTION
    1. Publishes Raizen.Endpoint.Service (single-file, win-x64) with the given version.
    2. Publishes Raizen.Endpoint.Tray    (single-file, win-x64) with the given version.
    3. Builds the WiX MSI, stamping the same version into the MSI package.

    Output: publish\installer\RaizenEndpoint.msi

    The version is embedded in the assembly so UpdateWorker can compare it against
    AgentDeployment:CurrentVersion on the server. The MSI version drives MajorUpgrade
    so Windows Installer correctly uninstalls the old version before installing the new one.

.PARAMETER Version
    Version string, e.g. "1.0.14". Defaults to Directory.Build.props RaizenVersion.

.EXAMPLE
    .\scripts\Build-EndpointMsi.ps1 -Version "1.0.14"
#>
param(
    [string] $Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Log  { param($msg) Write-Host "[Endpoint] $msg" -ForegroundColor Cyan }
function Fail { param($msg) Write-Error "[ERROR] $msg"; exit 1 }

$Root = Split-Path $PSScriptRoot -Parent
$propsPath = Join-Path $Root "Directory.Build.props"
if ([string]::IsNullOrWhiteSpace($Version)) {
    if (!(Test-Path $propsPath)) { Fail "Version was not provided and Directory.Build.props was not found." }
    [xml] $props = Get-Content $propsPath
    $Version = $props.Project.PropertyGroup.RaizenVersion
    if ([string]::IsNullOrWhiteSpace($Version)) { Fail "RaizenVersion is missing from Directory.Build.props." }
}

Log "Building Raizen Endpoint MSI v$Version"

# -- Publish Service -----------------------------------------------------------
Log "Publishing Endpoint Service (single-file win-x64)..."
& dotnet publish "$Root\src\Raizen.Endpoint\Raizen.Endpoint.Service" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o "$Root\publish\service" | Write-Host
if ($LASTEXITCODE -ne 0) { Fail "Service publish failed." }

# -- Publish Tray --------------------------------------------------------------
Log "Publishing Endpoint Tray (single-file win-x64)..."
& dotnet publish "$Root\src\Raizen.Endpoint\Raizen.Endpoint.Tray" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o "$Root\publish\tray" | Write-Host
if ($LASTEXITCODE -ne 0) { Fail "Tray publish failed." }

# -- Build MSI -----------------------------------------------------------------
Log "Building MSI (WiX 4)..."
$installerDir = "$Root\src\Raizen.Endpoint\Raizen.Endpoint.Installer"
$outMsi       = "$Root\publish\installer\RaizenEndpoint.msi"

New-Item -ItemType Directory -Path "$Root\publish\installer" -Force | Out-Null

Push-Location $installerDir
try {
    & wix build Package.wxs `
        -d "ServiceBinDir=$Root\publish\service" `
        -d "TrayBinDir=$Root\publish\tray" `
        -d "Version=$Version" `
        -ext WixToolset.Util.wixext `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Firewall.wixext `
        -o $outMsi | Write-Host
    if ($LASTEXITCODE -ne 0) { Fail "WiX build failed." }
} finally {
    Pop-Location
}

$sizeMb = [math]::Round((Get-Item $outMsi).Length / 1MB, 1)
Log ""
Log "+----------------------------------------------------------+"
Log "  MSI ready: publish\installer\RaizenEndpoint.msi ($sizeMb MB)"
Log "  Version:   $Version"
Log ""
Log "  Next steps:"
Log "  1. Copy MSI to the server's AgentDeployment:InstallerPath"
Log "  2. Set AgentDeployment:CurrentVersion = `"$Version`" in appsettings.Production.json"
Log "  3. Restart RaizenWeb service"
Log "  4. Endpoints will self-update within 4 hours"
Log "+----------------------------------------------------------+"
