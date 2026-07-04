$resp = Invoke-RestMethod -Uri 'http://localhost:5000/api/v1/actions' -Headers @{'X-Raizen-Dev-Token'='dev-admin-token'}
$copyFile = $resp | Where-Object { $_.actionType -eq 40 }
Write-Output "ID: $($copyFile.id)"
Write-Output "Parameters schema:"
$copyFile.parameters | ConvertTo-Json -Depth 5
