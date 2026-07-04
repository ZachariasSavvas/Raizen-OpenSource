# Kill any already-running server processes so the new binaries can bind the ports
Get-Process -Name 'Raizen.Server.Api','Raizen.Server.Web' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$base = 'C:\Users\user\source\repos\Raizen\publish'

# API
Start-Process powershell -ArgumentList "-NoProfile -WindowStyle Minimized -Command `"`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:ASPNETCORE_URLS='http://localhost:5001'; Set-Location '$base\api'; & '$base\api\Raizen.Server.Api.exe'`"" -WindowStyle Minimized

# Web
Start-Process powershell -ArgumentList "-NoProfile -WindowStyle Minimized -Command `"`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:ASPNETCORE_URLS='http://localhost:5002'; Set-Location '$base\web'; & '$base\web\Raizen.Server.Web.exe'`"" -WindowStyle Minimized

Start-Sleep -Seconds 4
Get-Process | Where-Object { $_.Name -in 'Raizen.Server.Api','Raizen.Server.Web' } | Select-Object Id,Name,StartTime
