<#
.SYNOPSIS
    Resets an admin user's password in the Raizen web portal.
.EXAMPLE
    .\Reset-AdminPassword.ps1
#>
$toolDir = "$PSScriptRoot\tools\ResetAdminPassword"
dotnet run --project $toolDir -c Release
