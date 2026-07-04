param(
    [string]$Token,
    [string]$Version = "1.5.2"
)

$headers = @{ Authorization = "token $Token"; Accept = 'application/vnd.github+json' }

# Get the release
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/ZachariasSavvas/Raizen/releases/tags/v$Version" -Headers $headers
Write-Host "Release: $($release.name) (ID $($release.id))"

# Delete existing assets
foreach ($asset in $release.assets) {
    Write-Host "Deleting existing asset: $($asset.name)..."
    Invoke-RestMethod -Uri "https://api.github.com/repos/ZachariasSavvas/Raizen/releases/assets/$($asset.id)" -Method Delete -Headers $headers
}

$uploadBase = $release.upload_url -replace '\{.*\}', ''

function Upload-Asset($filePath, $assetName) {
    $size = [math]::Round((Get-Item $filePath).Length / 1MB, 1)
    Write-Host "Uploading $assetName ($size MB)..."
    $url   = "${uploadBase}?name=$([Uri]::EscapeDataString($assetName))"
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $bytes -ContentType 'application/octet-stream' | Out-Null
    Write-Host "  Done."
}

Upload-Asset "release\RaizenServer-$Version.zip"                    "RaizenServer-$Version.zip"
Upload-Asset "release\RaizenServer-$Version\RaizenServer-Setup.exe" "RaizenServer-Setup-$Version.exe"
Upload-Asset "publish\installer\RaizenEndpoint.msi"                 "RaizenEndpoint-$Version.msi"

Write-Host ""
Write-Host "Assets updated: $($release.html_url)"
