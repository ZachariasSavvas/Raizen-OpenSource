#Requires -RunAsAdministrator
#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the Raizen Endpoint agent (Windows Service + Tray app autostart).

.DESCRIPTION
    Performs the following steps:
      1. Copies service and tray binaries to %ProgramFiles%\Raizen\
      2. Creates %ProgramData%\Raizen\raizen-config.json with the provided server URL
      3. Generates a machine-unique API key and outputs it for admin registration
      4. Installs RaizenEndpoint as a Windows Service (SYSTEM)
      5. Configures a startup shortcut for the tray app (All Users RunKey)
      6. Locks down ACLs on config dir (SYSTEM + Admins write; Users read)
      7. Sets ACLs on ApprovedScripts dir (SYSTEM + Admins write; no other write)

.PARAMETER ServerUrl
    The base URL of the Raizen API server, e.g. "https://raizen.contoso.com:5001"

.PARAMETER ServiceBinDir
    Directory containing the published Raizen.Endpoint.Service binaries.
    Defaults to .\publish\service

.PARAMETER TrayBinDir
    Directory containing the published Raizen.Endpoint.Tray binaries.
    Defaults to .\publish\tray

.PARAMETER InstallDir
    Destination install directory. Defaults to %ProgramFiles%\Raizen

.PARAMETER ConfigPath
    Override for config file location. Defaults to %ProgramData%\Raizen\raizen-config.json

.EXAMPLE
    .\Install-RaizenEndpoint.ps1 -ServerUrl "https://raizen.contoso.com:5001"
#>
[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory)]
    [ValidatePattern('^https?://.+')]
    [string] $ServerUrl,

    [string] $ServiceBinDir = ".\publish\service",
    [string] $TrayBinDir    = ".\publish\tray",
    [string] $InstallDir    = "$env:ProgramFiles\Raizen",
    [string] $ConfigPath    = "$env:ProgramData\Raizen\raizen-config.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "`n=== Raizen Endpoint Installer ===" -ForegroundColor Cyan

# ── 1. Validate prerequisites ──────────────────────────────────────────────────
if (-not (Test-Path $ServiceBinDir)) {
    throw "Service bin dir not found: $ServiceBinDir. Build the solution first."
}
if (-not (Test-Path $TrayBinDir)) {
    throw "Tray bin dir not found: $TrayBinDir. Build the solution first."
}

# ── 2. Install binaries ────────────────────────────────────────────────────────
$serviceDir  = Join-Path $InstallDir "Service"
$trayDir     = Join-Path $InstallDir "Tray"
$scriptsDir  = Join-Path $env:ProgramData "Raizen\ApprovedScripts"
$configDir   = Split-Path $ConfigPath -Parent
$logDir      = Join-Path $env:ProgramData "Raizen\Logs"

foreach ($dir in @($serviceDir, $trayDir, $scriptsDir, $configDir, $logDir)) {
    if ($PSCmdlet.ShouldProcess($dir, "Create directory")) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

Write-Host "Copying service binaries to $serviceDir ..." -ForegroundColor Gray
Copy-Item -Path "$ServiceBinDir\*" -Destination $serviceDir -Recurse -Force

Write-Host "Copying tray binaries to $trayDir ..." -ForegroundColor Gray
Copy-Item -Path "$TrayBinDir\*" -Destination $trayDir -Recurse -Force

# ── 3. Generate machine identity and API key ───────────────────────────────────
$machineId = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Cryptography' -Name 'MachineGuid').MachineGuid
$apiKey    = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)) `
              -replace '\+', '-' `
              -replace '/', '_' `
              -replace '=', ''

# ── 4. Write raizen-config.json ────────────────────────────────────────────────
$config = [ordered]@{
    ServerUrl               = $ServerUrl
    TlsPinThumbprint        = ""
    MachineId               = $machineId
    ApiKey                  = $apiKey
    PollIntervalSeconds     = 30
    HeartbeatIntervalSeconds = 300
    HttpProxy               = $null
    HttpTimeoutSeconds      = 30
}

if ($PSCmdlet.ShouldProcess($ConfigPath, "Write config")) {
    $config | ConvertTo-Json -Depth 5 | Set-Content -Path $ConfigPath -Encoding UTF8 -Force
    Write-Host "Config written to: $ConfigPath" -ForegroundColor Green
}

# ── 5. Lock down ACLs ─────────────────────────────────────────────────────────
function Set-RestrictedAcl {
    param([string]$Path, [bool]$AllowUsersRead = $false)

    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)  # disable inheritance

    $system = [System.Security.Principal.NTAccount]"NT AUTHORITY\SYSTEM"
    $admins = [System.Security.Principal.NTAccount]"BUILTIN\Administrators"
    $users  = [System.Security.Principal.NTAccount]"BUILTIN\Users"

    $fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
    $readOnly    = [System.Security.AccessControl.FileSystemRights]::ReadAndExecute
    $inherit     = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
    $none        = [System.Security.AccessControl.InheritanceFlags]::None
    $prop        = [System.Security.AccessControl.PropagationFlags]::None
    $allow       = [System.Security.AccessControl.AccessControlType]::Allow

    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, $fullControl, $inherit, $prop, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, $fullControl, $inherit, $prop, $allow)))

    if ($AllowUsersRead) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, $readOnly, $inherit, $prop, $allow)))
    }

    Set-Acl -Path $Path -AclObject $acl
}

if ($PSCmdlet.ShouldProcess($configDir, "Set ACLs")) {
    # NOTE: Users need read access because the tray app (runs as logged-in user) reads
    # the ApiKey from raizen-config.json to authenticate API calls. A future version will
    # split secrets into a SYSTEM-only file and use named-pipe IPC from the tray app.
    Set-RestrictedAcl -Path $configDir -AllowUsersRead $true
    Set-RestrictedAcl -Path $scriptsDir -AllowUsersRead $false  # Scripts: SYSTEM+Admins only
}

# ── 6. Install Windows Service ─────────────────────────────────────────────────
$serviceName = "RaizenEndpoint"
$serviceExe  = Join-Path $serviceDir "Raizen.Endpoint.Service.exe"
$serviceBin  = "`"$serviceExe`" --config=`"$ConfigPath`""

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..." -ForegroundColor Yellow
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep 2
}

if ($PSCmdlet.ShouldProcess($serviceName, "Create Windows Service")) {
    $svcParams = @{
        Name           = $serviceName
        BinaryPathName = $serviceBin
        DisplayName    = "Raizen Elevation Service"
        Description    = "Brokered JIT elevation agent. Executes approved actions as SYSTEM."
        StartupType    = "Automatic"
    }
    New-Service @svcParams | Out-Null

    # Set service to run as LocalSystem
    sc.exe config $serviceName obj= "LocalSystem" | Out-Null

    # Set failure recovery: restart on failure
    sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

    Start-Service -Name $serviceName
    Write-Host "Service '$serviceName' installed and started." -ForegroundColor Green
}

# ── 7. Tray autostart for all users (HKLM RunKey) ─────────────────────────────
$trayExe = Join-Path $trayDir "Raizen.Endpoint.Tray.exe"
$runKey  = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"

if ($PSCmdlet.ShouldProcess($runKey, "Register tray autostart")) {
    Set-ItemProperty -Path $runKey -Name "RaizenTray" `
        -Value "`"$trayExe`" --config=`"$ConfigPath`"" `
        -Type String
    Write-Host "Tray autostart registered." -ForegroundColor Green
}

# ── 8. Output API key for server registration ──────────────────────────────────
Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Yellow
Write-Host "  ENDPOINT REGISTRATION — save these values now!" -ForegroundColor Yellow
Write-Host "╠══════════════════════════════════════════════════════════════╣" -ForegroundColor Yellow
Write-Host "  Machine ID : $machineId" -ForegroundColor White
Write-Host "  API Key    : $apiKey" -ForegroundColor White
Write-Host "╠══════════════════════════════════════════════════════════════╣" -ForegroundColor Yellow
Write-Host "  Register this endpoint on the Raizen server by running:" -ForegroundColor Cyan
Write-Host "    POST $ServerUrl/api/v1/endpoints/register" -ForegroundColor Cyan
Write-Host "    Headers:" -ForegroundColor Cyan
Write-Host "      Authorization: Bearer <admin-token>" -ForegroundColor Cyan
Write-Host "      X-Raizen-MachineId: $machineId" -ForegroundColor Cyan
Write-Host "      X-Raizen-NewApiKey: $apiKey" -ForegroundColor Cyan
Write-Host "    Body: { `"MachineName`": `"$env:COMPUTERNAME`" }" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝`n" -ForegroundColor Yellow

# ── 9. Register Windows right-click context menus ──────────────────────────────
# NOTE: Uses Microsoft.Win32.Registry directly to avoid PowerShell provider hanging
#       on the '*' wildcard class.  MUIVerb (not Default) is required for cascade menus.
Write-Host "Registering right-click context menus..." -ForegroundColor Gray

$runAsAdminCmd    = "`"$trayExe`" --run-as-admin=`"%1`" --config=`"$ConfigPath`""
$fileTransferCmd  = "`"$trayExe`" --file-transfer=`"%1`" --config=`"$ConfigPath`""
$filePermsCmd     = "`"$trayExe`" --file-permissions=`"%1`" --config=`"$ConfigPath`""
$servicesCmd      = "`"$trayExe`" --services --config=`"$ConfigPath`""
$networkChangeCmd = "`"$trayExe`" --network-change --config=`"$ConfigPath`""
$envVarCmd        = "`"$trayExe`" --env-var --config=`"$ConfigPath`""

# Helper: registers a Raizen cascade menu on a shell class.
# $Verbs is an array of [label, verbKey, command] triples.
# IMPORTANT: Do NOT set any values on the 'shell' container key — doing so
# (e.g. setting MUIVerb there from a prior install) corrupts the cascade menu
# and causes Windows to show only a stray "command" item on folder right-clicks.
function Register-CascadeMenu {
    param(
        [Microsoft.Win32.RegistryKey]$Hive,
        [string]$Class,
        [object[][]]$Verbs
    )
    $base = "$Class\shell\Raizen"
    try { $Hive.DeleteSubKeyTree($base) } catch {}

    $k = $Hive.CreateSubKey($base)
    $k.SetValue('MUIVerb',     'Raizen')
    $k.SetValue('Icon',        "$trayExe,0")
    $k.SetValue('SubCommands', '')   # empty = use shell\ subkeys
    $k.Close()

    foreach ($v in $Verbs) {
        $label, $verbKey, $cmd = $v
        $sub = $Hive.CreateSubKey("$base\shell\$verbKey")
        $sub.SetValue('MUIVerb', $label)
        $sub.Close()
        $subc = $Hive.CreateSubKey("$base\shell\$verbKey\command")
        $subc.SetValue('', $cmd)
        $subc.Close()
    }

    Write-Host "  OK: $Class" -ForegroundColor Gray
}

# Production install runs as admin — write to HKCR (maps to HKLM\Software\Classes for system-wide)
$hkcr = [Microsoft.Win32.Registry]::ClassesRoot

# Executables: Run as Admin + File Transfer + File Permissions
if ($PSCmdlet.ShouldProcess("HKCR\exefile\shell\Raizen", "Register exe context menu")) {
    $exeVerbs = @(
        @('Request to Run as Admin',    'RunAsAdmin',    $runAsAdminCmd),
        @('Request File Transfer',      'FileTransfer',  $fileTransferCmd),
        @('Request File Permission',    'FilePerms',     $filePermsCmd),
        @('Request Network Change',     'NetworkChange', $networkChangeCmd),
        @('Request System Variable',    'EnvVar',        $envVarCmd),
        @('Services',                   'Services',      $servicesCmd)
    )
    foreach ($cls in @('exefile', 'Msi.Package', 'Microsoft.Management.Console')) {
        Register-CascadeMenu -Hive $hkcr -Class $cls -Verbs $exeVerbs
    }
}

# All other files: File Transfer + File Permissions
if ($PSCmdlet.ShouldProcess("HKCR\*\shell\Raizen", "Register generic file context menu")) {
    $fileVerbs = @(
        @('Request File Transfer',   'FileTransfer',  $fileTransferCmd),
        @('Request File Permission', 'FilePerms',     $filePermsCmd),
        @('Request Network Change',  'NetworkChange', $networkChangeCmd),
        @('Request System Variable', 'EnvVar',        $envVarCmd),
        @('Services',                'Services',      $servicesCmd)
    )
    Register-CascadeMenu -Hive $hkcr -Class '*' -Verbs $fileVerbs
}

# Folders: File Transfer + File Permissions + Services
if ($PSCmdlet.ShouldProcess("HKCR\Directory\shell\Raizen", "Register folder context menu")) {
    $dirVerbs = @(
        @('Request File Transfer',   'FileTransfer',  $fileTransferCmd),
        @('Request File Permission', 'FilePerms',     $filePermsCmd),
        @('Request Network Change',  'NetworkChange', $networkChangeCmd),
        @('Request System Variable', 'EnvVar',        $envVarCmd),
        @('Services',                'Services',      $servicesCmd)
    )
    Register-CascadeMenu -Hive $hkcr -Class 'Directory' -Verbs $dirVerbs
    Register-CascadeMenu -Hive $hkcr -Class 'Directory\Background' -Verbs $dirVerbs
}

Write-Host "Right-click Raizen context menus registered." -ForegroundColor Green

Write-Host "Installation complete." -ForegroundColor Green
