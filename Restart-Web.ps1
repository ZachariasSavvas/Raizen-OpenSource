Get-Process | Where-Object { $_.Name -in 'Raizen.Server.Api','Raizen.Server.Web' } | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "Stopped. Starting fresh..."
Start-Sleep 1

$env:ASPNETCORE_ENVIRONMENT = 'Development'

# API
$env:ASPNETCORE_URLS = 'http://localhost:5001'
Start-Process -FilePath 'C:\Users\user\source\repos\Raizen\publish\api\Raizen.Server.Api.exe' -WorkingDirectory 'C:\Users\user\source\repos\Raizen\publish\api' -WindowStyle Minimized

# Web
$env:ASPNETCORE_URLS = 'http://localhost:5002'
Start-Process -FilePath 'C:\Users\user\source\repos\Raizen\publish\web\Raizen.Server.Web.exe' -WorkingDirectory 'C:\Users\user\source\repos\Raizen\publish\web' -WindowStyle Minimized

Start-Sleep 4
$procs = Get-Process | Where-Object { $_.Name -in 'Raizen.Server.Api','Raizen.Server.Web' }
if ($procs) { $procs | Select-Object Id,Name } else { Write-Host "NOT running" }
