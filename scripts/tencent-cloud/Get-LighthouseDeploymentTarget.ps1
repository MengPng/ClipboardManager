$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $instances = Invoke-TencentCloudJsonApi -Service lighthouse -Action DescribeInstances -Version '2020-03-24' -Region 'ap-guangzhou' -Body @{ Limit = 100; Offset = 0 }
    if ($instances.TotalCount -ne 1) { throw "Expected exactly one Lighthouse instance, found $($instances.TotalCount)." }
    $instance = $instances.InstanceSet[0]
    $firewall = Invoke-TencentCloudJsonApi -Service lighthouse -Action DescribeFirewallRules -Version '2020-03-24' -Region 'ap-guangzhou' -Body @{ InstanceId = $instance.InstanceId; Limit = 100; Offset = 0 }
    [pscustomobject]@{
        InstanceId = [string]$instance.InstanceId
        InstanceName = [string]$instance.InstanceName
        State = [string]$instance.InstanceState
        LatestOperation = [string]$instance.LatestOperation
        LatestOperationState = [string]$instance.LatestOperationState
        LatestOperationRequestId = [string]$instance.LatestOperationRequestId
        PublicAddresses = @($instance.PublicAddresses)
        KeyIds = @($instance.KeyIds)
        Firewall = @($firewall.FirewallRuleSet | ForEach-Object { [pscustomobject]@{ Protocol=$_.Protocol; Port=$_.Port; Cidr=$_.CidrBlock; Action=$_.Action } })
    } | ConvertTo-Json -Depth 6
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
