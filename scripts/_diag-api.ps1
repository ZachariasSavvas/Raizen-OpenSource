$MachineId = 'fb245aba-4ee6-4f77-96b0-258606002080'
$ApiKey    = '6jMCxrrRnm6Pnsqmiz1MM_REmDCMCIl4VLRA62Uji_OIE3KTGAu2IGWVqHTCdiXf'
$Headers   = @{ 'X-Raizen-MachineId' = $MachineId; 'X-Raizen-ApiKey' = $ApiKey }

# Check API server process age
$proc = Get-Process -Name 'Raizen.Server.Api' -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "API server PID=$($proc.Id) started=$($proc.StartTime)"
} else {
    Write-Host 'API server NOT running!' -ForegroundColor Red
}

# Check published API binary timestamp
$pub = 'C:\Users\user\source\repos\Raizen\publish\api\Raizen.Server.Api.exe'
Write-Host "Published API binary: $((Get-Item $pub).LastWriteTime)"

# Test the mine endpoint to confirm tray auth works
Write-Host ''
Write-Host 'Testing /api/v1/requests/mine ...'
try {
    $r = Invoke-RestMethod 'http://localhost:5001/api/v1/requests/mine' -Headers $Headers
    Write-Host "Got $($r.items.Count) request(s)"
    if ($r.items.Count -gt 0) {
        $reqId = $r.items[0].id
        Write-Host "Top request: $reqId (status=$($r.items[0].status))"

        # Test the comments endpoint
        Write-Host "Testing /api/v1/requests/$reqId/comments ..."
        try {
            $comments = Invoke-RestMethod "http://localhost:5001/api/v1/requests/$reqId/comments" -Headers $Headers
            Write-Host "Comments returned: $($comments.Count)" -ForegroundColor Green
            $comments | ForEach-Object { Write-Host "  - IsAdmin=$($_.isAdmin) Body=$($_.body)" }
        } catch {
            Write-Host "Comments endpoint FAILED: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "(This means the API server is running the OLD binary without the comments endpoint)" -ForegroundColor Yellow
        }
    }
} catch {
    Write-Host "Mine endpoint FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
