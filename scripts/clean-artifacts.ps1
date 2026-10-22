#requires -Version 5.1
<#
.SYNOPSIS
    清理 artifacts/ 下的历史构建产物（默认仅预览，不删除）。

.DESCRIPTION
    仓库 artifacts/ 长期累积发布包、候选包、备份与中间发布产物，大量内容相互重复。
    本脚本按类别清理，所有删除操作默认走 WhatIf 预览；显式传 -Execute 才真正删除。

    清理类别（均可独立开关）：
    1. -RemoveExtracted           删除 artifacts/release/<版本>/ClipboardManager-*-win-x64/
                                 解包目录。其中 exe 与同目录扁平发布包逐字节一致（已核验哈希），
                                 保留 portable.zip / .exe / .msi + SHA256SUMS 即不丢内容。
    2. -RemoveSupersededCandidates 删除 0.7.8 候选目录（layout-fixed、topbar-spacing 等），
                                 最终 0.7.8 发布包已存在于 artifacts/release/0.7.8。
    3. -RemoveBackups             删除 artifacts/release-backups/（0.7.8 修复前的旧包备份）。
    4. -RemoveLegacyPublish       删除 artifacts/publish/ 下无版本号的早期目录
                                 （cloud-*、collapse-docking-fix、single-exe、msi-single 等）。
    5. -PruneOldVersions          仅保留 artifacts/release/ 最近 N 个版本（-KeepLastVersions，默认 2）。

.PARAMETER Execute
    真正执行删除。缺省时仅打印将删除的内容与可释放空间。

.EXAMPLE
    .\scripts\clean-artifacts.ps1                      # 预览全部默认类别
    .\scripts\clean-artifacts.ps1 -Execute             # 执行默认类别
    .\scripts\clean-artifacts.ps1 -PruneOldVersions -KeepLastVersions 3 -Execute
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Execute,
    [switch]$RemoveExtracted = $true,
    [switch]$RemoveSupersededCandidates = $true,
    [switch]$RemoveBackups = $true,
    [switch]$RemoveLegacyPublish = $true,
    [switch]$PruneOldVersions,
    [int]$KeepLastVersions = 2
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'

if (-not (Test-Path $artifacts)) { Write-Host 'artifacts/ 不存在，无需清理。'; exit 0 }

$targets = New-Object System.Collections.Generic.List[object]

# 1) release 版本内的解包重复目录
if ($RemoveExtracted) {
    Get-ChildItem -LiteralPath (Join-Path $artifacts 'release') -Force -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Force -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'ClipboardManager-*-win-x64' }
        } |
        ForEach-Object { $targets.Add([pscustomobject]@{ Category = 'ExtractedDuplicate'; Path = $_.FullName }) }
}

# 2) 已被最终 0.7.8 发布包取代的候选目录
if ($RemoveSupersededCandidates) {
    $candidates = @(
        'layout-fixed-0.7.8', 'topbar-spacing-0.7.8', 'topbar-spacing-final-0.7.8',
        'header-restore-0.7.8', 'todo-file-polish-0.7.8', 'history-divider-fix-0.7.8',
        'local-only-header-0.7.8', 'calculator-result-first-0.7.8'
    )
    foreach ($c in $candidates) {
        $p = Join-Path $artifacts $c
        if (Test-Path $p) { $targets.Add([pscustomobject]@{ Category = 'SupersededCandidate'; Path = $p }) }
    }
}

# 3) 发布前备份
if ($RemoveBackups) {
    $p = Join-Path $artifacts 'release-backups'
    if (Test-Path $p) { $targets.Add([pscustomobject]@{ Category = 'ReleaseBackup'; Path = $p }) }
}

# 4) 无版本号的早期 publish 目录
if ($RemoveLegacyPublish) {
    Get-ChildItem -LiteralPath (Join-Path $artifacts 'publish') -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^\d+\.\d+\.\d+' } |
        ForEach-Object { $targets.Add([pscustomobject]@{ Category = 'LegacyPublish'; Path = $_.FullName }) }
}

# 5) 仅保留最近 N 个 release 版本
if ($PruneOldVersions) {
    $versions = Get-ChildItem -LiteralPath (Join-Path $artifacts 'release') -Force -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '[^\d.]', '') } -Descending
    $drop = $versions | Select-Object -Skip $KeepLastVersions
    foreach ($v in $drop) { $targets.Add([pscustomobject]@{ Category = 'OldReleaseVersion'; Path = $v.FullName }) }
}

# 汇总
$summary = $targets | ForEach-Object {
    $bytes = (Get-ChildItem -LiteralPath $_.Path -Force -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    [pscustomobject]@{ Category = $_.Category; Path = $_.Path.Replace($root, '.'); SizeMB = [math]::Round($bytes / 1MB, 2) }
}
$summary | Sort-Object Category, Path | Format-Table -AutoSize
$totalMB = ($summary | Measure-Object SizeMB -Sum).Sum
Write-Host ("总计可释放：{0:N1} MB（{1} 个目标）" -f $totalMB, $summary.Count)

if (-not $Execute) {
    Write-Host ''
    Write-Host '当前为预览模式。确认无误后加 -Execute 执行删除。' -ForegroundColor Yellow
    exit 0
}

$failures = @()
foreach ($t in $targets) {
    try {
        if ($PSCmdlet.ShouldProcess($t.Path, 'Remove-Item -Recurse -Force')) {
            Remove-Item -LiteralPath $t.Path -Recurse -Force -ErrorAction Stop
        }
    } catch {
        $failures += "$($t.Path): $($_.Exception.Message)"
    }
}
Write-Host ("已删除 {0}/{1} 个目标。" -f ($targets.Count - $failures.Count), $targets.Count)
if ($failures) { $failures | ForEach-Object { Write-Warning $_ } }
