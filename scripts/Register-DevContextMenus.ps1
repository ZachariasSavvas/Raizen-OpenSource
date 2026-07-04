#Requires -Version 5.1
<#
.SYNOPSIS
    Registers (or removes) Raizen right-click context menus for dev testing.
    Writes to HKCU — no elevation required.

.PARAMETER TrayExe
    Path to Raizen.Endpoint.Tray.exe. Defaults to .\publish\tray\Raizen.Endpoint.Tray.exe

.PARAMETER ConfigPath
    Path to raizen-config.json. Defaults to .\publish\service\raizen-config.json

.PARAMETER Remove
    If specified, removes all Raizen context menu entries instead of registering them.
#>
param(
    [string]$TrayExe    = $null,
    [string]$ConfigPath = $null,
    [switch]$Remove
)

$root = Split-Path $PSScriptRoot -Parent

if (-not $TrayExe) {
    $TrayExe = Join-Path $root "publish\tray\Raizen.Endpoint.Tray.exe"
}
if (-not $ConfigPath) {
    $ConfigPath = Join-Path $root "publish\service\raizen-config.json"
}

$hkcu = [Microsoft.Win32.Registry]::CurrentUser
$classesBase = "SOFTWARE\Classes"

function Remove-RaizenMenus {
    foreach ($class in @('Directory', 'Directory\Background', '*', 'exefile', 'Msi.Package')) {
        $path = "$classesBase\$class\shell\Raizen"
        try { $hkcu.DeleteSubKeyTree($path) } catch {}
    }
    Write-Host "Raizen context menus removed from HKCU." -ForegroundColor Yellow
}

if ($Remove) {
    Remove-RaizenMenus
    return
}

if (-not (Test-Path $TrayExe)) {
    Write-Error "Tray exe not found: $TrayExe`nBuild first: dotnet publish src/Raizen.Endpoint/Raizen.Endpoint.Tray -c Release -r win-x64 -o publish/tray"
    exit 1
}

$trayExeQ   = "`"$TrayExe`""
$configArg  = if (Test-Path $ConfigPath) { " --config=`"$ConfigPath`"" } else { "" }

$runAsAdminCmd   = "$trayExeQ --run-as-admin=`"%1`"$configArg"
$fileTransferCmd = "$trayExeQ --file-transfer=`"%1`"$configArg"
$filePermsCmd    = "$trayExeQ --file-permissions=`"%1`"$configArg"
$networkCmd      = "$trayExeQ --network-change$configArg"
$envVarCmd       = "$trayExeQ --env-var$configArg"
$servicesCmd     = "$trayExeQ --services$configArg"

function Register-CascadeMenu {
    param([string]$Class, [object[][]]$Verbs)

    $base = "$classesBase\$Class\shell\Raizen"
    try { $hkcu.DeleteSubKeyTree($base) } catch {}

    $k = $hkcu.CreateSubKey($base)
    $k.SetValue('MUIVerb',     'Raizen')
    $k.SetValue('Icon',        "$TrayExe,0")
    $k.SetValue('SubCommands', '')
    $k.Close()

    foreach ($v in $Verbs) {
        $label, $verbKey, $cmd = $v
        $sub = $hkcu.CreateSubKey("$base\shell\$verbKey")
        $sub.SetValue('MUIVerb', $label)
        $sub.Close()
        $subc = $hkcu.CreateSubKey("$base\shell\$verbKey\command")
        $subc.SetValue('', $cmd)
        $subc.Close()
    }

    Write-Host "  Registered: $Class" -ForegroundColor Gray
}

Write-Host "Registering Raizen Explorer context menus (HKCU)..." -ForegroundColor Cyan
Write-Host "  Tray exe : $TrayExe" -ForegroundColor Gray
Write-Host "  Config   : $(if (Test-Path $ConfigPath) { $ConfigPath } else { '(not found, omitted)' })" -ForegroundColor Gray
Write-Host ""

# Folders
Register-CascadeMenu -Class 'Directory' -Verbs @(
    @('Request File Transfer',    'FileTransfer',  $fileTransferCmd),
    @('Request File Permission',  'FilePerms',     $filePermsCmd),
    @('Request Network Change',   'NetworkChange', $networkCmd),
    @('Request System Variable',  'EnvVar',        $envVarCmd),
    @('Services',                 'Services',      $servicesCmd)
)
Register-CascadeMenu -Class 'Directory\Background' -Verbs @(
    @('Request File Transfer',    'FileTransfer',  $fileTransferCmd),
    @('Request File Permission',  'FilePerms',     $filePermsCmd),
    @('Request Network Change',   'NetworkChange', $networkCmd),
    @('Request System Variable',  'EnvVar',        $envVarCmd),
    @('Services',                 'Services',      $servicesCmd)
)

# All files
Register-CascadeMenu -Class '*' -Verbs @(
    @('Request File Transfer',    'FileTransfer',  $fileTransferCmd),
    @('Request File Permission',  'FilePerms',     $filePermsCmd),
    @('Request Network Change',   'NetworkChange', $networkCmd),
    @('Request System Variable',  'EnvVar',        $envVarCmd),
    @('Services',                 'Services',      $servicesCmd)
)

# Executables
Register-CascadeMenu -Class 'exefile' -Verbs @(
    @('Request to Run as Admin',  'RunAsAdmin',    $runAsAdminCmd),
    @('Request File Transfer',    'FileTransfer',  $fileTransferCmd),
    @('Request File Permission',  'FilePerms',     $filePermsCmd),
    @('Request Network Change',   'NetworkChange', $networkCmd),
    @('Request System Variable',  'EnvVar',        $envVarCmd),
    @('Services',                 'Services',      $servicesCmd)
)

Write-Host ""
Write-Host "Done. Right-click any file or folder to see the Raizen menu." -ForegroundColor Green
Write-Host "To remove: .\scripts\Register-DevContextMenus.ps1 -Remove" -ForegroundColor Gray
