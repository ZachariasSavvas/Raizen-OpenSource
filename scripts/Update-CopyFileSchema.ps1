$headers = @{
    'X-Raizen-Dev-Token' = 'dev-admin-token'
    'Content-Type' = 'application/json'
}
$base = 'http://localhost:5000/api/v1/actions'

$all = Invoke-RestMethod -Uri $base -Headers $headers
$copyDefs = @($all | Where-Object { $_.actionType -eq 40 })

foreach ($def in $copyDefs) {
    Write-Output "Updating $($def.id)"

    $params = @(
        [ordered]@{ key='SourcePath';      displayName='Source Path';      description='Full path to the source file';    type=3; required=$true;  validationPattern=$null; defaultValue='' }
        [ordered]@{ key='DestinationPath'; displayName='Destination Path'; description='Destination folder or full path'; type=3; required=$true;  validationPattern=$null; defaultValue='' }
        [ordered]@{ key='Operation';       displayName='Operation';         description='Copy or Move';                    type=0; required=$false; validationPattern='^(Copy|Move)$'; defaultValue='Copy' }
    )

    $body = [ordered]@{
        displayName                     = $def.displayName
        description                     = $def.description
        actionType                      = $def.actionType
        approvalWindowMinutes           = $def.approvalWindowMinutes
        approverGroupIds                = @()
        autoApprove                     = $def.autoApprove
        autoApproveConditionDescription = $def.autoApproveConditionDescription
        isEnabled                       = $def.isEnabled
        parameters                      = $params
    }

    $json = $body | ConvertTo-Json -Depth 10
    $result = Invoke-RestMethod -Method Put -Uri "$base/$($def.id)" -Headers $headers -Body $json
    Write-Output "  OK - params: $(($result.parameters | ForEach-Object key) -join ', ')"
}
