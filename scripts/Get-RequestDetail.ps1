param([string]$Id)
$headers = @{ 'X-Raizen-Dev-Token' = 'dev-admin-token' }
$r = Invoke-RestMethod -Uri "http://localhost:5000/api/v1/requests/$Id" -Headers $headers
Write-Host "Status         : $($r.status)"
Write-Host "ExecutionResult: $($r.executionResult)"
Write-Host "ExecutionError : $($r.executionError)"
Write-Host "Parameters     : $($r.parameters | ConvertTo-Json -Compress)"
