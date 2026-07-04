[System.Environment]::SetEnvironmentVariable('RAIZEN_CONFIG_PATH', 'C:\Users\user\source\repos\Raizen\publish\service\raizen-config.json', 'Process')
Start-Process -FilePath 'C:\Users\user\source\repos\Raizen\publish\service\Raizen.Endpoint.Service.exe' -WorkingDirectory 'C:\Users\user\source\repos\Raizen\publish\service' -WindowStyle Minimized
Start-Sleep 2
Start-Process -FilePath 'C:\Users\user\source\repos\Raizen\publish\tray\Raizen.Endpoint.Tray.exe' -WorkingDirectory 'C:\Users\user\source\repos\Raizen\publish\tray' -WindowStyle Minimized
Start-Sleep 3
Get-Process | Where-Object { $_.Name -match 'Raizen' } | Select-Object Id,Name
