param(
    [Parameter(Mandatory)] [string]$PublicKeyPath,
    [Parameter(Mandatory)] [string]$InstanceId,
    [string]$Region = 'ap-guangzhou',
    [string]$KeyName = 'clipboardmanager_prod',
    [string]$Username = 'ubuntu'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $publicKey = (Get-Content -LiteralPath $PublicKeyPath -Raw).Trim()
    if (-not $publicKey.StartsWith('ssh-rsa ')) { throw 'Only an RSA OpenSSH public key is accepted by this deployment script.' }

    $existing = Invoke-TencentCloudJsonApi -Service lighthouse -Action DescribeKeyPairs -Version '2020-03-24' -Region $Region -Body @{ Limit=100; Offset=0; Filters=@(@{Name='key-name'; Values=@($KeyName)}) }
    if ($existing.TotalCount -gt 0) {
        $keyId = [string]$existing.KeyPairSet[0].KeyId
        $created = $false
    }
    else {
        $imported = Invoke-TencentCloudJsonApi -Service lighthouse -Action ImportKeyPair -Version '2020-03-24' -Region $Region -Body @{ KeyName=$KeyName; PublicKey=$publicKey }
        $keyId = [string]$imported.KeyId
        $created = $true
    }

    $association = Invoke-TencentCloudJsonApi -Service lighthouse -Action AssociateInstancesKeyPairs -Version '2020-03-24' -Region $Region -Body @{
        KeyIds=@($keyId)
        InstanceIds=@($InstanceId)
        AssociateType='ONLINE'
        Username=$Username
    }

    [pscustomobject]@{
        KeyId=$keyId
        Created=$created
        RequestId=[string]$association.RequestId
        AssociateType='ONLINE'
    } | ConvertTo-Json -Compress
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
