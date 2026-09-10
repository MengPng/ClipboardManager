$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $account = Invoke-TencentCloudJsonApi -Service cam -Action GetUserAppId -Version '2019-01-16' -Body @{}
    $python = Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) '.tmp\cos-sdk\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) { throw 'Isolated COS SDK environment is missing.' }
    $code = @'
import hashlib, json, os, uuid
from qcloud_cos import CosConfig, CosS3Client
region = "ap-guangzhou"
bucket = "clipboardmanager-" + os.environ["CLIPBOARD_COS_APPID"]
client = CosS3Client(CosConfig(Region=region, SecretId=os.environ["TENCENTCLOUD_SECRET_ID"], SecretKey=os.environ["TENCENTCLOUD_SECRET_KEY"]))
key = "acceptance/" + str(uuid.uuid4()) + ".txt"
payload = b"ClipboardManager COS round-trip acceptance"
client.put_object(Bucket=bucket, Key=key, Body=payload, ContentType="text/plain")
downloaded = client.get_object(Bucket=bucket, Key=key)["Body"].get_raw_stream().read()
if hashlib.sha256(downloaded).digest() != hashlib.sha256(payload).digest():
    raise RuntimeError("Downloaded object hash mismatch")
client.delete_object(Bucket=bucket, Key=key)
print(json.dumps({"Uploaded": True, "HashVerified": True, "Deleted": True}))
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($code))
    $env:CLIPBOARD_COS_APPID = [string]$account.AppId
    & $python -c "import base64; exec(base64.b64decode('$encoded'))"
    if ($LASTEXITCODE -ne 0) { throw 'COS round-trip acceptance failed.' }
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
    Remove-Item Env:CLIPBOARD_COS_APPID -ErrorAction SilentlyContinue
}
