$proc = Get-Process -Name 'Raizen.Endpoint.Tray' -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Process: PID=$($proc.Id) Started=$($proc.StartTime)"
    $path = $proc.MainModule.FileName
    Write-Host "Path: $path"
    Write-Host "Binary date: $((Get-Item $path).LastWriteTime)"
} else {
    Write-Host 'Tray process NOT running'
}

Write-Host ''
Write-Host '--- Published binary ---'
$pub = 'C:\Users\user\source\repos\Raizen\publish\tray\Raizen.Endpoint.Tray.exe'
if (Test-Path $pub) { Get-Item $pub | Select-Object FullName,LastWriteTime,@{N='SizeMB';E={[math]::Round($_.Length/1MB,1)}} }

Write-Host '--- Installed binary ---'
$inst = 'C:\Program Files\Raizen\Tray\Raizen.Endpoint.Tray.exe'
if (Test-Path $inst) { Get-Item $inst | Select-Object FullName,LastWriteTime,@{N='SizeMB';E={[math]::Round($_.Length/1MB,1)}} }
else { Write-Host 'Not found at Program Files path' }
