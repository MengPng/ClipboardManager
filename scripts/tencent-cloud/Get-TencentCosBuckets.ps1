$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"

try {
    $python = Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) '.tmp\cos-sdk\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) { throw 'Isolated COS SDK environment is missing.' }
    $code = @'
import json, os
from qcloud_cos import CosConfig, CosS3Client
client = CosS3Client(CosConfig(Region="ap-guangzhou", SecretId=os.environ["TENCENTCLOUD_SECRET_ID"], SecretKey=os.environ["TENCENTCLOUD_SECRET_KEY"]))
result = client.list_buckets()
buckets = (result.get("Buckets") or {}).get("Bucket") or []
owner = result.get("Owner") or {}
print(json.dumps({"OwnerId": owner.get("ID"), "Buckets": [{"Name": b.get("Name"), "Location": b.get("Location"), "CreationDate": b.get("CreationDate")} for b in buckets]}, ensure_ascii=False))
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($code))
    & $python -c "import base64; exec(base64.b64decode('$encoded'))"
    if ($LASTEXITCODE -ne 0) { throw 'COS SDK request failed.' }
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
