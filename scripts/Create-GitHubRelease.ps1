param(
    [string]$Token,
    [string]$Version = "1.5.9",
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
    body       = "## Raizen v$Version`n`n### Highlights`n- Added an admin Hardening Check page for production readiness review.`n- Added high-risk action policy so shells, scripts, identity, trust, file-delete, registry, network, and installer actions always require human review.`n- Added clearer high-risk warnings in the Action Catalog, Auto-Approval Rules, Pending Approvals, and Request Detail pages.`n- Configured Windows service recovery for RaizenApi and RaizenWeb during setup and updates, with endpoint script recovery reinforcement.`n- Removed temporary local diagnostic/test scripts and hard-coded dev-token helpers from the open-source repo.`n`n### Files`n- **RaizenServer-$Version.zip** - Full Windows server package.`n- **RaizenServer-Setup-$Version.exe** - Standalone server setup wizard.`n- **RaizenEndpoint-$Version.msi** - Endpoint agent installer.`n- **SHA256SUMS-$Version.txt** - Release asset checksums.`n`n### Upgrade`nExtract the server package and run **Update-RaizenServer.ps1** as Administrator with **-AgentVersion $Version**. Pilot the endpoint update before broad deployment."
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
