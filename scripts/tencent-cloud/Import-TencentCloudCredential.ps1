param([string]$CredentialPath = "$env:LOCALAPPDATA\ClipboardManager\secrets\tencent-cloud.dpapi.json")

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $CredentialPath -PathType Leaf)) { throw "Tencent Cloud credential file was not found." }
$payload = Get-Content -LiteralPath $CredentialPath -Raw | ConvertFrom-Json
if ($payload.provider -notin @('TencentCloud', 'tencent-cloud') -or $payload.protection -ne 'Windows DPAPI CurrentUser' -or
    [string]::IsNullOrWhiteSpace($payload.secret_id) -or
    [string]::IsNullOrWhiteSpace($payload.secret_key_dpapi)) { throw "Tencent Cloud credential file has an unsupported format." }

try {
    try {
        $secureKey = ConvertTo-SecureString -String $payload.secret_key_dpapi -ErrorAction Stop
        $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
        $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    }
    catch [FormatException] {
        $protectedBytes = [Convert]::FromBase64String([string]$payload.secret_key_dpapi)
        $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
            $protectedBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $plainKey = [Text.Encoding]::UTF8.GetString($plainBytes)
    }
    $env:TENCENTCLOUD_SECRET_ID = [string]$payload.secret_id
    $env:TENCENTCLOUD_SECRET_KEY = $plainKey
    $env:TENCENTCLOUD_CAM_USER = [string]$payload.cam_user
}
finally {
    if ($null -ne $keyPointer -and $keyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer) }
    if ($null -ne $plainBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($plainBytes) }
    if ($null -ne $protectedBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($protectedBytes) }
    $plainKey = $null
    if ($null -ne $secureKey) { $secureKey.Dispose() }
}

# Intentionally writes no credential data. Dot-source only in a short-lived deployment process.
