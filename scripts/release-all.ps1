#Requires -Version 7.2
<#
.SYNOPSIS
  一条命令完成正式发布：检查 → 构建测试打包 → 同步 GitHub（推送、标签、Release）→ 更新下载页与 latest.json。

.DESCRIPTION
  发布前需已提交：版本号（Directory.Build.targets、release/release-contract.json）、CHANGELOG、release/notes/<Version>.md。
  GitHub Release 与下载服务器使用同一批本地产物，两处 SHA-256 一致。
  中途失败可直接重跑：已存在且指向当前提交的标签会复用，已存在的 Release 会覆盖上传资产。

.EXAMPLE
  .\scripts\release-all.ps1 -Version 0.9.0 -DryRun     # 只构建并生成下载页文件，不推送、不上传
  .\scripts\release-all.ps1 -Version 0.9.0             # 正式发布（需 gh 已登录、CM_DEPLOY_SSH 已设置）
  .\scripts\release-all.ps1 -Version 0.9.0 -SkipBuild  # 重跑时复用 artifacts/release/0.9.0
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$SshTarget = $env:CM_DEPLOY_SSH,
    [string]$SshKey = $env:CM_DEPLOY_KEY,
    [switch]$IncludeMsi,
    [switch]$SkipBuild,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$tag = "v$Version"
$releaseDir = "artifacts/release/$Version"

# 1. 发布前检查
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号须为 MAJOR.MINOR.PATCH。" }
$contract = Get-Content "release/release-contract.json" -Raw | ConvertFrom-Json
$prefix = ([xml](Get-Content "Directory.Build.targets" -Raw)).Project.PropertyGroup.VersionPrefix
if ($contract.appVersion -ne $Version -or $prefix -ne $Version) {
    throw "版本不一致：release-contract.json=$($contract.appVersion)，Directory.Build.targets=$prefix，本次发布=$Version。"
}
if (-not (Test-Path "release/notes/$Version.md")) { throw "缺少面向用户的更新说明 release/notes/$Version.md。" }
if ((git rev-parse --abbrev-ref HEAD) -ne "main") { throw "只能从 main 分支发布。" }
if (git status --porcelain) { throw "工作区有未提交改动。先提交（建议信息：release: $tag），再发布。" }
if (-not $DryRun -and -not $SshTarget) { throw "缺少下载服务器 SSH 目标：设置环境变量 CM_DEPLOY_SSH（如 ubuntu@1.2.3.4）或传 -SshTarget。" }
if (-not $DryRun) { gh auth status | Out-Null }

git fetch origin main --tags
$behind = [int](git rev-list --count HEAD..origin/main)
if ($behind -gt 0) { throw "本地 main 落后 origin/main $behind 个提交，先 git pull --rebase 并重新验证。" }
$head = git rev-parse HEAD
if ((git tag -l $tag) -and (git rev-list -n 1 $tag) -ne $head) { throw "标签 $tag 已存在且不指向当前提交，禁止覆盖；请升版本号。" }

# 2. 构建、测试、打包
if ($SkipBuild) {
    if (-not (Test-Path "$releaseDir/SHA256SUMS.txt")) { throw "-SkipBuild 需要已有 $releaseDir。" }
} else {
    dotnet build ClipboardManager.sln --configuration Release -p:NuGetAudit=false
    dotnet test ClipboardManager.sln --configuration Release --no-build -p:NuGetAudit=false
    & ./scripts/build-release.ps1 -Version $Version -IncludeMsi:$IncludeMsi
}
$assets = @(
    "$releaseDir/ClipboardManager-$Version-win-x64.exe"
    "$releaseDir/ClipboardManager-$Version-win-x64-portable.zip"
    "$releaseDir/SHA256SUMS.txt"
)
if ($IncludeMsi) { $assets += "$releaseDir/ClipboardManager-$Version-win-x64.msi" }
$assets | ForEach-Object { if (-not (Test-Path $_)) { throw "缺少发布文件 $_。" } }

if ($DryRun) {
    & ./scripts/publish-download.ps1 -Version $Version -IncludeMsi:$IncludeMsi -DryRun
    Write-Output "DryRun 完成：产物 $releaseDir，下载页文件 artifacts/download-site。未推送 GitHub、未上传服务器。"
    return
}

# 3. 同步 GitHub：推送 main 与标签，创建或更新 Release（与下载页同一批文件）
git push origin main
if (-not (git tag -l $tag)) { git tag -a $tag -m "ClipboardManager $Version" }
git push origin $tag
$existing = gh release list --limit 200 --json tagName --jq ".[].tagName"
if ($existing -contains $tag) {
    gh release upload $tag @assets --clobber
} else {
    gh release create $tag @assets --title "ClipboardManager $Version" --notes-file "release/notes/$Version.md" --verify-tag
}

# 4. 更新下载服务器；latest.json 最后切换，客户端只会看到已完整上传的版本
& ./scripts/publish-download.ps1 -Version $Version -SshTarget $SshTarget -SshKey $SshKey -IncludeMsi:$IncludeMsi

$url = gh release view $tag --json url --jq ".url"
Write-Output "发布完成 $Version"
Write-Output "  GitHub Release：$url"
Write-Output "  下载页：https://clipboard.netkaize.com/download/"
Get-Content "$releaseDir/SHA256SUMS.txt" | ForEach-Object { Write-Output "  $_" }
