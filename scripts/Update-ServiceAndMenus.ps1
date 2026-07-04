#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Swaps the Raizen Endpoint Service binary and rebuilds context menus from scratch.
    Run once after each code update.
#>

$repoRoot   = 'C:\Users\user\source\repos\Raizen'
$trayExe    = "$repoRoot\publish\tray\Raizen.Endpoint.Tray.exe"
$configPath = 'C:\ProgramData\Raizen\raizen-config.json'
$serviceDir = "$repoRoot\publish\service"
$newBinDir  = "$repoRoot\publish\service-new"

# ── 1. Swap service binary ─────────────────────────────────────────────────────
Write-Host "Stopping RaizenEndpoint service..." -ForegroundColor Cyan
Stop-Service RaizenEndpoint -Force -ErrorAction SilentlyContinue
Start-Sleep 2

Write-Host "Copying new binaries to $serviceDir ..." -ForegroundColor Cyan
Copy-Item "$newBinDir\*" $serviceDir -Recurse -Force

Write-Host "Starting RaizenEndpoint service..." -ForegroundColor Cyan
Start-Service RaizenEndpoint
Write-Host "Service running." -ForegroundColor Green

# ── 2. Rebuild context menus ───────────────────────────────────────────────────
Write-Host "Rebuilding context menus..." -ForegroundColor Cyan

$runAdminCmd  = "`"$trayExe`" --run-as-admin=`"%1`"   --config=`"$configPath`""
$fileXferCmd  = "`"$trayExe`" --file-transfer=`"%1`"   --config=`"$configPath`""
$filePermsCmd = "`"$trayExe`" --file-permissions=`"%1`" --config=`"$configPath`""
$servicesCmd  = "`"$trayExe`" --services               --config=`"$configPath`""

function Set-RaizenMenu {
    param(
        [Microsoft.Win32.RegistryKey]$Hive,
        [string]$Class,
        # Array of [Label, VerbKey, Command] triples
        [object[][]]$Verbs
    )
    $base = "$Class\shell\Raizen"

    # Wipe the entire old subtree first (handles any legacy keys)
    try { $Hive.DeleteSubKeyTree($base) } catch {}

    # Parent cascade node
    $parent = $Hive.CreateSubKey($base)
    $parent.SetValue('',            '')
    $parent.SetValue('MUIVerb',     'Raizen')
    $parent.SetValue('Icon',        "$trayExe,0")
    $parent.SetValue('SubCommands', '')   # empty = enumerate shell\ subkeys
    $parent.Close()

    foreach ($v in $Verbs) {
        # Ensure $v is the inner string triple, not a wrapped array
        if ($v -is [System.Array] -and $v[0] -is [System.Array]) { $v = $v[0] }
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

$hkcr = [Microsoft.Win32.Registry]::ClassesRoot

# Executables — all three options
$exeVerbs = @(
    @('Request to Run as Admin',    'RunAsAdmin',   $runAdminCmd),
    @('Request File Transfer',   'FileTransfer', $fileXferCmd),
    @('Request File Permission', 'FilePerms',    $filePermsCmd),
    @('Services',                'Services',     $servicesCmd)
)
foreach ($cls in @('exefile', 'Msi.Package', 'Microsoft.Management.Console')) {
    Set-RaizenMenu -Hive $hkcr -Class $cls -Verbs $exeVerbs
}

# All other files — transfer + permissions
$fileVerbs = @(
    @('Request File Transfer',   'FileTransfer', $fileXferCmd),
    @('Request File Permission', 'FilePerms',    $filePermsCmd),
    @('Services',                'Services',     $servicesCmd)
)
Set-RaizenMenu -Hive $hkcr -Class '*' -Verbs $fileVerbs

# Folders — transfer + permissions + services
$dirVerbs = @(
    @('Request File Transfer',   'FileTransfer', $fileXferCmd),
    @('Request File Permission', 'FilePerms',    $filePermsCmd),
    @('Services',                'Services',     $servicesCmd)
)
Set-RaizenMenu -Hive $hkcr -Class 'Directory' -Verbs $dirVerbs
Set-RaizenMenu -Hive $hkcr -Class 'Directory\Background' -Verbs $dirVerbs

Write-Host "Context menus registered." -ForegroundColor Green

# ── 3. Restart Explorer to flush shell menu cache ─────────────────────────────
Write-Host "Restarting Explorer..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep 2
Start-Process explorer.exe
Write-Host "Done. Right-click any .exe to test." -ForegroundColor Green
