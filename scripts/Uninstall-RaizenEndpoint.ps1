#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Fully uninstalls the Raizen Security Endpoint agent from this machine (start clean).

.DESCRIPTION
    1. Kills the tray process if running.
    2. Uninstalls via MSI (preferred) -- automatically removes the service, tray
       autostart, context menus, and firewall rules registered by the installer.
    3. Falls back to manual cleanup for each component if the MSI is not registered.
    4. Removes %ProgramData%\Raizen (config, logs, approved scripts) by default.
       Pass -KeepConfig to preserve that data.

.PARAMETER KeepConfig
    Skip removal of %ProgramData%\Raizen. Useful when reinstalling on the same
    machine and you want to keep the existing registration / API key.

.EXAMPLE
    # Full clean uninstall (default):
    .\Uninstall-RaizenEndpoint.ps1

    # Reinstall scenario -- keep config:
    .\Uninstall-RaizenEndpoint.ps1 -KeepConfig
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$KeepConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'   # don't abort on individual failures

$serviceName  = 'RaizenEndpoint'
$installDir   = "$env:ProgramFiles\Raizen"
$configDir    = "$env:ProgramData\Raizen"
$trayExeName  = 'Raizen.Endpoint.Tray'
$upgradeCode  = '{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}'

# Context-menu HKCR keys added by the MSI
$contextMenuRoots = @(
    'Registry::HKEY_CLASSES_ROOT\exefile\shell\Raizen',
    'Registry::HKEY_CLASSES_ROOT\Msi.Package\shell\Raizen',
    'Registry::HKEY_CLASSES_ROOT\Microsoft.Management.Console\shell\Raizen',
    'Registry::HKEY_CLASSES_ROOT\*\shell\Raizen',
    'Registry::HKEY_CLASSES_ROOT\Directory\shell\Raizen',
    'Registry::HKEY_CLASSES_ROOT\Directory\Background\shell\Raizen'
)

function Step { param($msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok   { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Warn { param($msg) Write-Host "    [--] $msg" -ForegroundColor Yellow }
function Fail { param($msg) Write-Host "    [!!] $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "Raizen Security Endpoint -- Full Uninstaller" -ForegroundColor White
Write-Host "=============================================" -ForegroundColor White

# ── 1. Kill tray process ──────────────────────────────────────────────────────
Step "Stopping tray application..."
$trayProcs = Get-Process -Name $trayExeName -ErrorAction SilentlyContinue
if ($trayProcs) {
    if ($PSCmdlet.ShouldProcess($trayExeName, "Kill process")) {
        $trayProcs | Stop-Process -Force
        Ok "Tray process terminated."
    }
} else {
    Warn "Tray process not running."
}

# ── 2. MSI uninstall (preferred path) ────────────────────────────────────────
Step "Looking for installed MSI (Raizen Security Endpoint)..."

$msiProductCode = $null

# Search the Uninstall registry for a product with our display name or upgrade code
$uninstallPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
foreach ($path in $uninstallPaths) {
    if (-not (Test-Path $path)) { continue }
    $found = Get-ChildItem $path -ErrorAction SilentlyContinue |
        Where-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            $props.DisplayName -eq 'Raizen Security Endpoint'
        } | Select-Object -First 1
    if ($found) {
        $msiProductCode = $found.PSChildName
        break
    }
}

if ($msiProductCode) {
    Ok "Found MSI product: $msiProductCode"
    if ($PSCmdlet.ShouldProcess($msiProductCode, "MSI uninstall (msiexec /x)")) {
        Write-Host "    Running msiexec /x $msiProductCode /qn ..." -ForegroundColor Gray
        $proc = Start-Process -FilePath 'msiexec.exe' `
            -ArgumentList "/x `"$msiProductCode`" /qn /norestart" `
            -Wait -PassThru -NoNewWindow
        if ($proc.ExitCode -eq 0) {
            Ok "MSI uninstall completed successfully."
            $msiHandled = $true
        } else {
            Fail "msiexec exited with code $($proc.ExitCode). Falling back to manual cleanup."
            $msiHandled = $false
        }
    } else {
        $msiHandled = $false   # -WhatIf
    }
} else {
    Warn "MSI registration not found. Using manual cleanup."
    $msiHandled = $false
}

# ── 3. Manual fallback cleanup ────────────────────────────────────────────────
# Skip each step if MSI already handled it.

# 3a. Windows Service
Step "Removing Windows Service ($serviceName)..."
if (-not $msiHandled) {
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($PSCmdlet.ShouldProcess($serviceName, "Stop and delete service")) {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            & sc.exe delete $serviceName | Out-Null
            Ok "Service removed."
        }
    } else {
        Warn "Service not found."
    }
} else {
    Ok "Handled by MSI uninstall."
}

# 3b. Tray autostart registry value
Step "Removing tray autostart (HKLM Run key)..."
if (-not $msiHandled) {
    $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    if (Get-ItemProperty -Path $runKey -Name 'RaizenTray' -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess($runKey, "Remove RaizenTray value")) {
            Remove-ItemProperty -Path $runKey -Name 'RaizenTray' -ErrorAction SilentlyContinue
            Ok "Autostart entry removed."
        }
    } else {
        Warn "Autostart entry not found."
    }
} else {
    Ok "Handled by MSI uninstall."
}

# 3c. Context-menu shell extensions
Step "Removing context-menu shell extensions..."
if (-not $msiHandled) {
    foreach ($key in $contextMenuRoots) {
        if (Test-Path $key) {
            if ($PSCmdlet.ShouldProcess($key, "Remove registry key")) {
                Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue
                Ok "Removed: $key"
            }
        } else {
            Warn "Not found: $key"
        }
    }
} else {
    Ok "Handled by MSI uninstall."
}

# 3d. Windows Firewall rules
Step "Removing Windows Firewall rules..."
if (-not $msiHandled) {
    $fwRules = @('Raizen Elevation Service (outbound)', 'Raizen Tray Agent (outbound)')
    foreach ($rule in $fwRules) {
        $exists = Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
        if ($exists) {
            if ($PSCmdlet.ShouldProcess($rule, "Remove firewall rule")) {
                Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
                Ok "Removed firewall rule: $rule"
            }
        } else {
            Warn "Firewall rule not found: $rule"
        }
    }
} else {
    Ok "Handled by MSI uninstall."
}

# 3e. RaizenSecurity registry hive (firewall KeyPath + any other settings)
Step "Removing HKLM\SOFTWARE\RaizenSecurity..."
$rzRegKey = 'HKLM:\SOFTWARE\RaizenSecurity'
if (Test-Path $rzRegKey) {
    if ($PSCmdlet.ShouldProcess($rzRegKey, "Remove registry key")) {
        Remove-Item -Path $rzRegKey -Recurse -Force -ErrorAction SilentlyContinue
        Ok "Registry hive removed."
    }
} else {
    Warn "Registry hive not found (already clean)."
}

# 3f. Install directory
Step "Removing install directory ($installDir)..."
if (-not $msiHandled) {
    if (Test-Path $installDir) {
        if ($PSCmdlet.ShouldProcess($installDir, "Remove directory")) {
            Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue
            Ok "Install directory removed."
        }
    } else {
        Warn "Install directory not found."
    }
} else {
    # Even after MSI uninstall, check for leftover files (e.g. log files in install dir)
    if (Test-Path $installDir) {
        if ($PSCmdlet.ShouldProcess($installDir, "Remove leftover install directory")) {
            Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue
            Ok "Leftover install directory removed."
        }
    } else {
        Ok "Install directory already removed by MSI."
    }
}

# ── 4. ProgramData (config, logs, approved scripts) ──────────────────────────
Step "Removing config and data directory ($configDir)..."
if ($KeepConfig) {
    Warn "Skipped (--KeepConfig specified). Data preserved at: $configDir"
} else {
    if (Test-Path $configDir) {
        if ($PSCmdlet.ShouldProcess($configDir, "Remove config and logs")) {
            Remove-Item -Path $configDir -Recurse -Force -ErrorAction SilentlyContinue
            Ok "Config and logs removed."
        }
    } else {
        Warn "Config directory not found (already clean)."
    }
}

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=============================================" -ForegroundColor White
if ($KeepConfig) {
    Write-Host "Raizen Endpoint uninstalled.  Config preserved at $configDir" -ForegroundColor Green
} else {
    Write-Host "Raizen Endpoint fully uninstalled. Machine is clean." -ForegroundColor Green
}
Write-Host ""
