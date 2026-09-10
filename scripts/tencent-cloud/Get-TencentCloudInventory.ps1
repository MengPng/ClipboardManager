$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $regionResponse = Invoke-TencentCloudJsonApi 'cvm' 'DescribeRegions' '2017-03-12' '' @{}
    $summary = [ordered]@{ Cvm = 0; PostgreSql = 0; Lighthouse = 0; LighthouseCapacity = @(); RegionsWithResources = @(); PermissionErrors = @() }
    foreach ($regionItem in $regionResponse.RegionSet) {
        $region = [string]$regionItem.Region
        $regionTotal = 0
        foreach ($probe in @(
            @{ Name='Cvm'; Service='cvm'; Action='DescribeInstances'; Version='2017-03-12'; Total='TotalCount' },
            @{ Name='PostgreSql'; Service='postgres'; Action='DescribeDBInstances'; Version='2017-03-12'; Total='TotalCount' },
            @{ Name='Lighthouse'; Service='lighthouse'; Action='DescribeInstances'; Version='2020-03-24'; Total='TotalCount' }
        )) {
            try {
                $result = Invoke-TencentCloudJsonApi $probe.Service $probe.Action $probe.Version $region @{ Limit=100; Offset=0 }
                $count = [int]$result.($probe.Total)
                $summary[$probe.Name] += $count
                $regionTotal += $count
                if ($probe.Name -eq 'Lighthouse' -and $count -gt 0) {
                    foreach ($instance in $result.InstanceSet) {
                        $summary.LighthouseCapacity += [ordered]@{
                            Region = $region
                            State = [string]$instance.InstanceState
                            Cpu = [int]$instance.CPU
                            MemoryGb = [int]$instance.Memory
                            SystemDiskGb = [int]$instance.SystemDisk.DiskSize
                            Os = [string]$instance.OsName
                        }
                    }
                }
            }
            catch {
                $code = if ($_.Exception.Message -like 'TencentApi:*') { $_.Exception.Message.Substring(11) } else { 'RequestFailed' }
                $summary.PermissionErrors += "$($probe.Name):${region}:$code"
            }
        }
        if ($regionTotal -gt 0) { $summary.RegionsWithResources += $region }
    }
    [pscustomobject]$summary | ConvertTo-Json -Compress
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
