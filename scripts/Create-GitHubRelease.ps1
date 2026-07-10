param(
    [string]$Token,
    [string]$Version = "1.5.8",
    [string]$Repository = "ZachariasSavvas/Raizen-OpenSource"
)

$headers = @{
    Authorization = "token $Token"
    Accept        = 'application/vnd.github+json'
}

# 1. Create the release (notes kept simple to avoid JSON escaping issues)
Write-Host "[1/4] Creating GitHub release v$Version..."

$releaseBody = [PSCustomObject]@{
    tag_name   = "v$Version"
    name       = "Raizen v$Version - Open Source Edition"
    body       = "## Raizen v$Version`n`n### Highlights`n- Endpoint health, process, and Windows service inventory with bounded low-frequency collection.`n- Configurable monitoring rules, per-service alerts, acknowledgement, deletion, and email routing.`n- Approved event-log diagnostic bundles with redaction, integrity verification, and automatic expiry.`n- Hardened audit-chain startup, append-only database enforcement, and idempotent schema repair.`n- Expanded admin exports, notification handling, endpoint status visibility, and tray request feedback.`n`n### Files`n- **RaizenServer-$Version.zip** - Full Windows server package.`n- **RaizenServer-Setup-$Version.exe** - Standalone server setup wizard.`n- **RaizenEndpoint-$Version.msi** - Endpoint agent installer.`n- **SHA256SUMS-$Version.txt** - Release asset checksums.`n`n### Upgrade`nExtract the server package and run **Update-RaizenServer.ps1** as Administrator with **-AgentVersion $Version**. Pilot the endpoint update before broad deployment."
    draft      = $false
    prerelease = $false
}

$body = $releaseBody | ConvertTo-Json -Depth 3 -Compress

$release = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/$Repository/releases" `
    -Method Post `
    -Headers $headers `
    -Body $body `
    -ContentType 'application/json; charset=utf-8'

Write-Host "  Release created: $($release.html_url)"

$uploadBase = $release.upload_url -replace '\{.*\}', ''

function Upload-Asset($filePath, $assetName) {
    $size = [math]::Round((Get-Item $filePath).Length / 1MB, 1)
    Write-Host "[uploading] $assetName ($size MB)..."
    $url = "${uploadBase}?name=$([Uri]::EscapeDataString($assetName))"
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    Invoke-RestMethod `
        -Uri $url `
        -Method Post `
        -Headers $headers `
        -Body $bytes `
        -ContentType 'application/octet-stream' | Out-Null
    Write-Host "  Uploaded."
}

Write-Host "[2/4] Uploading server package..."
Upload-Asset "release\RaizenServer-$Version.zip" "RaizenServer-$Version.zip"

Write-Host "[3/4] Uploading server setup installer..."
Upload-Asset "release\RaizenServer-$Version\RaizenServer-Setup.exe" "RaizenServer-Setup-$Version.exe"

Write-Host "[4/4] Uploading endpoint MSI..."
Upload-Asset "publish\installer\RaizenEndpoint.msi" "RaizenEndpoint-$Version.msi"

Write-Host ""
Write-Host "Done: $($release.html_url)"
