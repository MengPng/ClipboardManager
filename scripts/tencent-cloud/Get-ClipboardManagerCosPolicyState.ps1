$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $user = Invoke-TencentCloudJsonApi -Service cam -Action GetUser -Version '2019-01-16' -Body @{ Name=$env:TENCENTCLOUD_CAM_USER }
    $attached = Invoke-TencentCloudJsonApi -Service cam -Action ListAttachedUserPolicies -Version '2019-01-16' -Body @{ TargetUin=[int64]$user.Uin; Rp=200; Page=1 }
    $policy = @($attached.List | Where-Object { $_.PolicyName -eq 'ClipboardManagerCosAccess' }) | Select-Object -First 1
    if (-not $policy) { throw 'ClipboardManager COS policy is not attached to the credential user.' }
    $detail = Invoke-TencentCloudJsonApi -Service cam -Action GetPolicy -Version '2019-01-16' -Body @{ PolicyId=[int64]$policy.PolicyId }
    $document = [Uri]::UnescapeDataString([string]$detail.PolicyDocument) | ConvertFrom-Json
    [pscustomobject]@{
        CamUser=[string]$user.Name
        Uin=[string]$user.Uin
        PolicyId=[int64]$policy.PolicyId
        Attached=$true
        Actions=@($document.statement.action | ForEach-Object { $_ })
        Resources=@($document.statement.resource | ForEach-Object { $_ })
    } | ConvertTo-Json -Depth 8
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
