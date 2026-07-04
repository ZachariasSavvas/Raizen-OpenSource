$machineId = 'fb245aba-4ee6-4f77-96b0-258606002080'
$apiKey    = 'ab23ee61001570464f079a88f4b2c576ce2faa2355776791cbb78214e26b2c47'
$base      = 'http://localhost:5000/api/v1'

$epHeaders = @{
    'X-Raizen-MachineId' = $machineId
    'X-Raizen-ApiKey'    = $apiKey
    'Content-Type'       = 'application/json'
}
$adminHeaders = @{
    'X-Raizen-Dev-Token' = 'dev-admin-token'
    'Content-Type'       = 'application/json'
}

# Ensure source file exists
if (-not (Test-Path 'C:\test.ps1')) {
    Set-Content 'C:\test.ps1' 'Write-Host Hello from test.ps1'
    Write-Host 'Created C:\test.ps1'
}

if (-not (Test-Path 'D:\')) {
    Write-Host 'D:\ not found - aborting' -ForegroundColor Red; exit 1
}

# Get CopyFile action id
$actions    = Invoke-RestMethod -Uri "$base/actions" -Headers $adminHeaders
$copyAction = $actions | Where-Object { $_.actionType -eq 40 } | Select-Object -First 1
Write-Host "Action: $($copyAction.displayName)  id=$($copyAction.id)" -ForegroundColor Cyan

function Test-Transfer([string]$Op, [string]$Src, [string]$Dst) {
    Write-Host "`n=== $Op : $Src -> $Dst ===" -ForegroundColor Yellow

    $body = @{
        actionDefinitionId = $copyAction.id
        justification      = "Automated test - $Op"
        parameters         = @{ SourcePath = $Src; DestinationPath = $Dst; Operation = $Op }
    } | ConvertTo-Json -Depth 5

    try {
        $req = Invoke-RestMethod -Method Post -Uri "$base/requests" -Headers $epHeaders -Body $body
        Write-Host "  Submitted  id=$($req.id)" -ForegroundColor Gray
    } catch {
        Write-Host "  SUBMIT FAILED: $_" -ForegroundColor Red; return
    }

    try {
        $approveBody = @{ approved = $true; note = 'Auto-approved for test' } | ConvertTo-Json
        $approved = Invoke-RestMethod -Method Post -Uri "$base/requests/$($req.id)/review" -Headers $adminHeaders -Body $approveBody
        Write-Host "  Approved   status=$($approved.status)" -ForegroundColor Gray
    } catch {
        Write-Host "  APPROVE FAILED: $_" -ForegroundColor Red; return
    }

    Write-Host '  Polling for execution...' -ForegroundColor Gray
    for ($i = 1; $i -le 12; $i++) {
        Start-Sleep 5
        $s = Invoke-RestMethod -Uri "$base/requests/$($req.id)" -Headers $adminHeaders
        Write-Host "  [$($i*5)s] $($s.status)"
        if ($s.status -in 'Succeeded','Failed','Expired') {
            if ($s.executionResult) { Write-Host "  Result : $($s.executionResult)" -ForegroundColor Green }
            if ($s.executionError)  { Write-Host "  Error  : $($s.executionError)"  -ForegroundColor Red   }
            break
        }
    }

    Write-Host "  C:\test.ps1 exists: $(Test-Path 'C:\test.ps1')"
    Write-Host "  D:\test.ps1 exists: $(Test-Path 'D:\test.ps1')"
}

# ── Test 1: Copy ──────────────────────────────────────────────────────────────
Test-Transfer -Op 'Copy' -Src 'C:\test.ps1' -Dst 'D:\'

# ── Test 2: Move ──────────────────────────────────────────────────────────────
if (-not (Test-Path 'C:\test.ps1')) {
    Set-Content 'C:\test.ps1' 'Write-Host Hello from test.ps1'
    Write-Host 'Re-created C:\test.ps1 for Move test'
}
Test-Transfer -Op 'Move' -Src 'C:\test.ps1' -Dst 'D:\'
