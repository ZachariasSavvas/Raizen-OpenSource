Stop-Service RaizenEndpoint -Force -ErrorAction SilentlyContinue
Get-Process -Name 'Raizen.Endpoint.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$ServiceSrc = 'C:\Users\user\source\repos\Raizen\publish\service'
$TraySrc    = 'C:\Users\user\source\repos\Raizen\publish\tray'
$ServiceDst = 'C:\Program Files\Raizen\Service'
$TrayDst    = 'C:\Program Files\Raizen\Tray'

Copy-Item -Force "$ServiceSrc\Raizen.Endpoint.Service.exe" "$ServiceDst\"
Copy-Item -Force "$ServiceSrc\appsettings.json"            "$ServiceDst\"
if (Test-Path "$ServiceSrc\Raizen.Endpoint.Service.pdb") {
    Copy-Item -Force "$ServiceSrc\Raizen.Endpoint.Service.pdb" "$ServiceDst\"
}

$trayFiles = @(
    'Raizen.Endpoint.Tray.exe','Raizen.Endpoint.Tray.dll',
    'Raizen.Endpoint.Tray.deps.json','Raizen.Endpoint.Tray.runtimeconfig.json',
    'Raizen.Endpoint.Shared.dll','Raizen.Shared.dll','Raizen.ico'
)
foreach ($f in $trayFiles) {
    $src = Join-Path $TraySrc $f
    if (Test-Path $src) { Copy-Item -Force $src "$TrayDst\" }
}

Start-Service RaizenEndpoint
Start-Sleep -Seconds 2
$svc = Get-Service RaizenEndpoint
Write-Host "Service: $($svc.Status)"

Start-Process -FilePath "$TrayDst\Raizen.Endpoint.Tray.exe" -ArgumentList '--config="C:\ProgramData\Raizen\raizen-config.json"'
Write-Host 'Tray launched.'
