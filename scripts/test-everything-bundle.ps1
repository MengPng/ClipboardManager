#Requires -Version 7.2
<#
.SYNOPSIS
  核对随 EXE 内嵌的 Everything 安装程序（ADR-0007 附录）：文件 SHA-256 与 NOTICE.txt、EverythingSetup.cs 三者一致，
  许可证文件存在且为 MIT 文本，Authenticode 签名有效且签名者是 voidtools。任一项不符即停止发布。
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root "src/ClipboardManager.Windows/Assets/Everything"
$notice = Join-Path $dir "NOTICE.txt"
$license = Join-Path $dir "License.txt"
$code = Join-Path $root "src/ClipboardManager.Windows/Services/FileSearch/EverythingSetup.cs"

if (-not (Test-Path $notice) -or -not (Test-Path $license)) { throw "缺少 Everything 的 NOTICE.txt 或 License.txt。" }
$fileName = ((Select-String -Path $notice -Pattern '^File:\s*(\S+)').Matches[0].Groups[1].Value)
$expected = ((Select-String -Path $notice -Pattern '^SHA256:\s*([0-9A-Fa-f]{64})').Matches[0].Groups[1].Value).ToUpperInvariant()
$installer = Join-Path $dir $fileName
if (-not (Test-Path $installer)) { throw "缺少 Everything 安装程序 $fileName。" }

$actual = (Get-FileHash $installer -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "Everything 安装程序 SHA-256 与 NOTICE.txt 不一致：$actual ≠ $expected。" }
$source = Get-Content $code -Raw
if ($source -notmatch "InstallerSha256 = `"$expected`"") { throw "EverythingSetup.InstallerSha256 与 NOTICE.txt 不一致。" }
if ($source -notmatch "InstallerFileName = `"$([regex]::Escape($fileName))`"") { throw "EverythingSetup.InstallerFileName 与 NOTICE.txt 不一致。" }
$csproj = Get-Content (Join-Path $root "src/ClipboardManager.Windows/ClipboardManager.Windows.csproj") -Raw
if ($csproj -notmatch [regex]::Escape("Assets\Everything\$fileName")) { throw "csproj 没有内嵌 $fileName。" }

$licenseText = Get-Content $license -Raw
if ($licenseText -notmatch 'Permission is hereby granted, free of charge' -or $licenseText -notmatch 'David Carpenter') {
    throw "Everything 的 License.txt 不是预期的 MIT 许可文本。"
}

$signature = Get-AuthenticodeSignature $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'voidtools') {
    throw "Everything 安装程序签名无效或签名者不是 voidtools（$($signature.Status)）。"
}
Write-Output "Everything 安装程序校验通过：$fileName $actual"
