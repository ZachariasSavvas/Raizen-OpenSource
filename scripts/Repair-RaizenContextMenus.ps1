#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
    Re-registers Raizen right-click context menus, fixing any corruption from old installs.

.DESCRIPTION
    Deletes and recreates the Raizen shell context menu entries under HKCR.
    Use this when the context menu shows only "command" or doesn't work on folders.

.PARAMETER TrayExe
    Full path to Raizen.Endpoint.Tray.exe.
    Auto-detected from %ProgramFiles%\Raizen\Tray\ or .\publish\tray\ if omitted.

.PARAMETER ConfigPath
    Full path to raizen-config.json.
    Defaults to %ProgramData%\Raizen\raizen-config.json.

.EXAMPLE
    # Auto-detect (production install)
    .\Repair-RaizenContextMenus.ps1

.EXAMPLE
    # Dev build
    .\Repair-RaizenContextMenus.ps1 -TrayExe ".\publish\tray\Raizen.Endpoint.Tray.exe"
#>
param(
    [string] $TrayExe    = "",
    [string] $ConfigPath = "$env:ProgramData\Raizen\raizen-config.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Auto-detect tray exe ────────────────────────────────────────────────────────
if (-not $TrayExe) {
    $candidates = @(
        "$env:ProgramFiles\Raizen\Tray\Raizen.Endpoint.Tray.exe",
        (Join-Path $PSScriptRoot "..\publish\tray\Raizen.Endpoint.Tray.exe")
    )
    $TrayExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $TrayExe) {
        throw "Cannot find Raizen.Endpoint.Tray.exe. Specify -TrayExe '<path>'."
    }
}

$TrayExe = (Resolve-Path $TrayExe).Path
Write-Host "Tray exe    : $TrayExe"   -ForegroundColor Gray
Write-Host "Config path : $ConfigPath" -ForegroundColor Gray

# ── Build command strings ────────────────────────────────────────────────────────
$runAsAdminCmd    = "`"$TrayExe`" --run-as-admin=`"%1`" --config=`"$ConfigPath`""
$fileTransferCmd  = "`"$TrayExe`" --file-transfer=`"%1`" --config=`"$ConfigPath`""
$filePermsCmd     = "`"$TrayExe`" --file-permissions=`"%1`" --config=`"$ConfigPath`""
$servicesCmd      = "`"$TrayExe`" --services --config=`"$ConfigPath`""
$networkChangeCmd = "`"$TrayExe`" --network-change --config=`"$ConfigPath`""
$envVarCmd        = "`"$TrayExe`" --env-var --config=`"$ConfigPath`""

# ── Register-CascadeMenu ────────────────────────────────────────────────────────
# Deletes any existing Raizen entry on $Class and recreates it cleanly.
# $Verbs: array of [label, verbKey, command] triples.
function Register-CascadeMenu {
    param(
        [Microsoft.Win32.RegistryKey] $Hive,
        [string]                      $Class,
        [object[][]]                  $Verbs
    )
    $base = "$Class\shell\Raizen"

    # Delete old entry completely (handles any leftover structure from old installs)
    try { $Hive.DeleteSubKeyTree($base) } catch {}

    # Create the parent cascade key
    $parent = $Hive.CreateSubKey($base)
    $parent.SetValue('MUIVerb',     'Raizen')
    $parent.SetValue('Icon',        "$TrayExe,0")
    $parent.SetValue('SubCommands', '')   # empty = use shell\ subkeys
    $parent.Close()

    # Create each sub-verb.  Note: NO values are set on the 'shell' container key —
    # setting MUIVerb there was what caused the "command" corruption on Directory.
    foreach ($v in $Verbs) {
        $label, $verbKey, $cmd = $v
        $sub = $Hive.CreateSubKey("$base\shell\$verbKey")
        $sub.SetValue('MUIVerb', $label)
        $sub.Close()
        $subc = $Hive.CreateSubKey("$base\shell\$verbKey\command")
        $subc.SetValue('', $cmd)
        $subc.Close()
    }

    Write-Host "  Registered: $Class" -ForegroundColor Gray
}

# ── Verb sets ────────────────────────────────────────────────────────────────────
$exeVerbs = @(
    @('Request to Run as Admin', 'RunAsAdmin',    $runAsAdminCmd),
    @('Request File Transfer',   'FileTransfer',  $fileTransferCmd),
    @('Request File Permission', 'FilePerms',     $filePermsCmd),
    @('Request Network Change',  'NetworkChange', $networkChangeCmd),
    @('Request System Variable', 'EnvVar',        $envVarCmd),
    @('Services',                'Services',      $servicesCmd)
)

$fileVerbs = @(
    @('Request File Transfer',   'FileTransfer',  $fileTransferCmd),
    @('Request File Permission', 'FilePerms',     $filePermsCmd),
    @('Request Network Change',  'NetworkChange', $networkChangeCmd),
    @('Request System Variable', 'EnvVar',        $envVarCmd),
    @('Services',                'Services',      $servicesCmd)
)

$dirVerbs = @(
    @('Request File Transfer',   'FileTransfer',  $fileTransferCmd),
    @('Request File Permission', 'FilePerms',     $filePermsCmd),
    @('Request Network Change',  'NetworkChange', $networkChangeCmd),
    @('Request System Variable', 'EnvVar',        $envVarCmd),
    @('Services',                'Services',      $servicesCmd)
)

# ── Write to HKCR (= HKLM\Software\Classes when elevated) ───────────────────────
Write-Host "`nRegistering context menus..." -ForegroundColor Cyan
$hkcr = [Microsoft.Win32.Registry]::ClassesRoot

foreach ($cls in @('exefile', 'Msi.Package', 'Microsoft.Management.Console')) {
    Register-CascadeMenu -Hive $hkcr -Class $cls -Verbs $exeVerbs
}

Register-CascadeMenu -Hive $hkcr -Class '*'                    -Verbs $fileVerbs
Register-CascadeMenu -Hive $hkcr -Class 'Directory'            -Verbs $dirVerbs
Register-CascadeMenu -Hive $hkcr -Class 'Directory\Background' -Verbs $dirVerbs

Write-Host "`nDone. Restart Explorer to see the updated menus." -ForegroundColor Green
Write-Host "(Or sign out and back in.)" -ForegroundColor Gray
