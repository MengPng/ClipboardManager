$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    if (-not $env:TENCENTCLOUD_CAM_USER) { throw 'CAM user name is missing from the protected credential file.' }
    $user = Invoke-TencentCloudJsonApi -Service cam -Action GetUser -Version '2019-01-16' -Body @{ Name=$env:TENCENTCLOUD_CAM_USER }
    $appid = '100024042494'
    $bucket = "clipboardmanager-$appid"
    $document = @{
        version='2.0'
        statement=@(
            @{ effect='allow'; action=@('cos:GetService','cos:PutBucket'); resource='*' },
            @{ effect='allow'; action=@('cos:HeadBucket','cos:GetBucket','cos:PutBucketLifecycle','cos:GetBucketLifecycle','cos:PutObject','cos:GetObject','cos:HeadObject','cos:DeleteObject','cos:InitiateMultipartUpload','cos:UploadPart','cos:ListParts','cos:ListMultipartUploads','cos:CompleteMultipartUpload','cos:AbortMultipartUpload'); resource=@("qcs::cos:ap-guangzhou:uid/${appid}:${bucket}","qcs::cos:ap-guangzhou:uid/${appid}:${bucket}/*") }
        )
    } | ConvertTo-Json -Compress -Depth 8
    $created = Invoke-TencentCloudJsonApi -Service cam -Action CreatePolicy -Version '2019-01-16' -Body @{
        PolicyName='ClipboardManagerCosAccess'
        Description='ClipboardManager production private bucket and object access'
        PolicyDocument=[Uri]::EscapeDataString($document)
    }
    $attached = Invoke-TencentCloudJsonApi -Service cam -Action AttachUserPolicy -Version '2019-01-16' -Body @{ PolicyId=[int64]$created.PolicyId; AttachUin=[int64]$user.Uin }
    [pscustomobject]@{PolicyId=[int64]$created.PolicyId; Attached=$true; RequestId=[string]$attached.RequestId} | ConvertTo-Json -Compress
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
