$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    if (-not $env:TENCENTCLOUD_CAM_USER) { throw 'CAM user name is missing from the protected credential file.' }
    $user = Invoke-TencentCloudJsonApi -Service cam -Action GetUser -Version '2019-01-16' -Body @{ Name=$env:TENCENTCLOUD_CAM_USER }
    $account = Invoke-TencentCloudJsonApi -Service cam -Action GetUserAppId -Version '2019-01-16' -Body @{}
    $appid = [string]$account.AppId
    $bucket = "clipboardmanager-$appid"
    $document = @{
        version='2.0'
        statement=@(
            @{ effect='allow'; action=@('name/cos:GetService','name/cos:PutBucket'); resource='*' },
            @{ effect='allow'; action=@('name/cos:HeadBucket','name/cos:GetBucket','name/cos:PutBucketLifecycle','name/cos:GetBucketLifecycle','name/cos:PutObject','name/cos:GetObject','name/cos:HeadObject','name/cos:DeleteObject','name/cos:InitiateMultipartUpload','name/cos:UploadPart','name/cos:ListParts','name/cos:ListMultipartUploads','name/cos:CompleteMultipartUpload','name/cos:AbortMultipartUpload'); resource=@("qcs::cos:ap-guangzhou:uid/${appid}:${bucket}","qcs::cos:ap-guangzhou:uid/${appid}:${bucket}/*") }
        )
    } | ConvertTo-Json -Compress -Depth 8
    $policies = Invoke-TencentCloudJsonApi -Service cam -Action ListPolicies -Version '2019-01-16' -Body @{ Rp=200; Page=1; Scope='Local' }
    $existing = @($policies.List | Where-Object { $_.PolicyName -eq 'ClipboardManagerCosAccess' }) | Select-Object -First 1
    if ($existing) {
        $policyId = [int64]$existing.PolicyId
        $updated = Invoke-TencentCloudJsonApi -Service cam -Action UpdatePolicy -Version '2019-01-16' -Body @{ PolicyId=$policyId; PolicyDocument=$document }
        $requestId = [string]$updated.RequestId
    } else {
        $created = Invoke-TencentCloudJsonApi -Service cam -Action CreatePolicy -Version '2019-01-16' -Body @{
            PolicyName='ClipboardManagerCosAccess'
            Description='ClipboardManager production private bucket and object access'
            PolicyDocument=$document
        }
        $policyId = [int64]$created.PolicyId
        $attached = Invoke-TencentCloudJsonApi -Service cam -Action AttachUserPolicy -Version '2019-01-16' -Body @{ PolicyId=$policyId; AttachUin=[int64]$user.Uin }
        $requestId = [string]$attached.RequestId
    }
    [pscustomobject]@{PolicyId=$policyId; Attached=$true; Updated=[bool]$existing; RequestId=$requestId} | ConvertTo-Json -Compress
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
