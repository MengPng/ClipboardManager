#Requires -Version 7.2
<#
.SYNOPSIS
  可选的代码签名：配置了证书就用 signtool 给文件签名并加时间戳，没有配置时只给出警告（产物未签名）。

.DESCRIPTION
  证书来源（二选一，不得写入仓库）：
    CM_SIGN_CERT_THUMBPRINT   当前用户或本机证书存储中的证书指纹（推荐，包括 USB 令牌 / 云 HSM 的证书）
    CM_SIGN_PFX + CM_SIGN_PFX_PASSWORD   PFX 文件路径与密码
  可选：CM_SIGN_TIMESTAMP_URL（默认 http://timestamp.digicert.com）、CM_SIGNTOOL（signtool.exe 路径）。
  -Require 时没有证书直接失败。
#>
param(
    [Parameter(Mandatory)][string[]]$Path,
    [switch]$Require
)
$ErrorActionPreference = "Stop"

$thumbprint = $env:CM_SIGN_CERT_THUMBPRINT
$pfx = $env:CM_SIGN_PFX
if (-not $thumbprint -and -not $pfx) {
    if ($Require) { throw "要求签名，但没有配置 CM_SIGN_CERT_THUMBPRINT 或 CM_SIGN_PFX。" }
    Write-Warning "未配置代码签名证书，产物未签名（Windows SmartScreen 可能提示未知发布者）。"
    return
}

$signtool = $env:CM_SIGNTOOL
if (-not $signtool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin"
    $signtool = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool -or -not (Test-Path $signtool)) { throw "找不到 signtool.exe：安装 Windows SDK，或设置 CM_SIGNTOOL。" }

$timestamp = if ($env:CM_SIGN_TIMESTAMP_URL) { $env:CM_SIGN_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
$certArgs = if ($thumbprint) { @('/sha1', $thumbprint) } else { @('/f', $pfx, '/p', $env:CM_SIGN_PFX_PASSWORD) }

foreach ($file in $Path) {
    & $signtool sign /fd SHA256 /td SHA256 /tr $timestamp @certArgs $file
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$file" }
    $signature = Get-AuthenticodeSignature $file
    if ($signature.Status -ne 'Valid') { throw "签名后校验失败：$file（$($signature.Status)）" }
    Write-Output "已签名：$(Split-Path $file -Leaf)  $($signature.SignerCertificate.Subject)"
}
