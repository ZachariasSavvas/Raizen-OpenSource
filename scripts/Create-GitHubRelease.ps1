param(
    [string]$Token,
    [string]$Version = "1.5.2"
)

$headers = @{
    Authorization = "token $Token"
    Accept        = 'application/vnd.github+json'
}

# 1. Create the release (notes kept simple to avoid JSON escaping issues)
Write-Host "[1/4] Creating GitHub release v$Version..."

$releaseBody = [PSCustomObject]@{
    tag_name   = "v$Version"
    name       = "Raizen v$Version"
    body       = "## Raizen v$Version`n`n### Patch Highlights`n- **Cleaner endpoint context menu**: the MSI now makes the primary Explorer path `Raizen -> Request to Run as Admin` for `.exe`, `.msi`, and `.msc` files without the older broad file/folder menu clutter.`n- **Run-as-admin reliability**: approved `.msi` and `.msc` launches are handled through the correct Windows host process instead of failing like a normal executable launch.`n- **Endpoint health visibility**: the Endpoints page now surfaces poll-signing status, poll failures, update check status, and update errors reported by agents.`n- **Safer release versioning**: endpoint service, tray, MSI, and release package builds now default to the shared `Directory.Build.props` version.`n- **Admin portal polish**: dark-mode persistence, pending-request detail overflow, and login lockout behavior are improved.`n- **Release documentation cleanup**: removed the hardcoded GitHub token example from release instructions.`n`n### Files`n- **RaizenServer-$Version.zip** - Full server package (API + Web + setup wizard + endpoint MSI)`n- **RaizenServer-Setup-$Version.exe** - Standalone server installer / setup wizard`n- **RaizenEndpoint-$Version.msi** - Endpoint agent installer (v$Version, auto-update compatible)`n`n### Rollout Guidance`n1. Extract **RaizenServer-$Version.zip** for server updates or fresh offline installs.`n2. Run **Update-RaizenServer.ps1** as Administrator with `-AgentVersion $Version`.`n3. Use **RaizenServer-Setup-$Version.exe** when you only need the server setup wizard installer.`n4. Confirm one pilot endpoint updates and reports healthy before pushing the update to all outdated endpoints."
    draft      = $false
    prerelease = $false
}

$body = $releaseBody | ConvertTo-Json -Depth 3 -Compress

$release = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/ZachariasSavvas/Raizen/releases" `
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
