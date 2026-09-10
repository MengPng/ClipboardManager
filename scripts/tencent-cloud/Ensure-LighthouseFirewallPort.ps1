param(
    [Parameter(Mandatory)] [string]$InstanceId,
    [Parameter(Mandatory)] [ValidateRange(1,65535)] [int]$Port,
    [string]$Region = 'ap-guangzhou',
    [string]$Description = 'ClipboardManager web service'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $current = Invoke-TencentCloudJsonApi -Service lighthouse -Action DescribeFirewallRules -Version '2020-03-24' -Region $Region -Body @{ InstanceId=$InstanceId; Limit=100; Offset=0 }
    $exists = @($current.FirewallRuleSet | Where-Object { $_.Protocol -eq 'TCP' -and $_.Port -eq "$Port" -and $_.Action -eq 'ACCEPT' -and $_.CidrBlock -eq '0.0.0.0/0' }).Count -gt 0
    if (-not $exists) {
        $created = Invoke-TencentCloudJsonApi -Service lighthouse -Action CreateFirewallRules -Version '2020-03-24' -Region $Region -Body @{
            InstanceId=$InstanceId
            FirewallVersion=$current.FirewallVersion
            FirewallRules=@(@{Protocol='TCP'; Port="$Port"; CidrBlock='0.0.0.0/0'; Action='ACCEPT'; FirewallRuleDescription=$Description})
        }
    }
    [pscustomobject]@{ Port=$Port; AlreadyPresent=$exists; RequestId=if ($created) { [string]$created.RequestId } else { $null } } | ConvertTo-Json -Compress
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
