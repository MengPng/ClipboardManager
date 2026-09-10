param([string]$Region = 'ap-guangzhou')

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $account = Invoke-TencentCloudJsonApi -Service cam -Action GetUserAppId -Version '2019-01-16' -Body @{}
    $python = Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) '.tmp\cos-sdk\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) { throw 'Isolated COS SDK environment is missing.' }
    $code = @'
import json, os
from qcloud_cos import CosConfig, CosS3Client
region = os.environ.get("CLIPBOARD_COS_REGION", "ap-guangzhou")
client = CosS3Client(CosConfig(Region=region, SecretId=os.environ["TENCENTCLOUD_SECRET_ID"], SecretKey=os.environ["TENCENTCLOUD_SECRET_KEY"]))
listed = client.list_buckets()
buckets = (listed.get("Buckets") or {}).get("Bucket") or []
appid = os.environ["CLIPBOARD_COS_APPID"]
bucket = "clipboardmanager-" + appid
created = False
if not any(x.get("Name") == bucket for x in buckets):
    client.create_bucket(Bucket=bucket, ACL="private")
    created = True
client.put_bucket_lifecycle(Bucket=bucket, LifecycleConfiguration={"Rule": [{"ID": "abort-incomplete-uploads", "Status": "Enabled", "Filter": {"Prefix": ""}, "AbortIncompleteMultipartUpload": {"DaysAfterInitiation": "7"}}]})
print(json.dumps({"Bucket": bucket, "Region": region, "Created": created}))
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($code))
    $env:CLIPBOARD_COS_REGION = $Region
    $env:CLIPBOARD_COS_APPID = [string]$account.AppId
    & $python -c "import base64; exec(base64.b64decode('$encoded'))"
    if ($LASTEXITCODE -ne 0) { throw 'COS bucket configuration failed.' }
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
    Remove-Item Env:CLIPBOARD_COS_REGION -ErrorAction SilentlyContinue
    Remove-Item Env:CLIPBOARD_COS_APPID -ErrorAction SilentlyContinue
}
