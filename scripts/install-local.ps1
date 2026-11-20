#Requires -Version 7.2
<#
.SYNOPSIS
  发布完成后把本机正在用的剪切板程序替换为 artifacts/release/<版本> 中的正式 EXE，并重新启动。

.DESCRIPTION
  1. 按 SHA256SUMS.txt 校验单文件 EXE，复制到固定目录（默认 %LOCALAPPDATA%\Programs\ClipboardManager），
     恢复助手一并复制。不放在 %LOCALAPPDATA%\ClipboardManager.App（旧 MSI 目录，那里的程序不能应用内更新）。
  2. 结束正在运行的 ClipboardManager*.exe（任何位置的开发构建、预览版、旧版本）。
  3. 本程序的开机启动值（HKCU Run\ClipboardManager）已存在时改指向新位置；不存在时不新增，也不改其他启动项。
  4. 开始菜单「本地剪切板管理工具」快捷方式指向新位置（没有则新建）。
  5. 检查旧 MSI 安装（0.9.5 之前，%LOCALAPPDATA%\ClipboardManager.App）：旧版保存设置时会把开机启动项改回它自己。
     默认只警告；加 -RemoveLegacyMsi 时静默卸载（只删旧程序目录与旧快捷方式，不动用户数据和开机启动项）。
  6. 启动新程序。用户数据在 %LOCALAPPDATA%\ClipboardManager，不受影响。
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs/ClipboardManager"),
    [switch]$RemoveLegacyMsi,
    [switch]$NoStart
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $root "artifacts/release/$Version"
$exeName = "ClipboardManager-$Version-win-x64.exe"
$exe = Join-Path $releaseDir $exeName
$helper = Join-Path $releaseDir "ClipboardManager-$Version-win-x64/ClipboardManager.Maintenance.exe"
$sums = Join-Path $releaseDir "SHA256SUMS.txt"
foreach ($required in $exe, $helper, $sums) { if (-not (Test-Path $required)) { throw "缺少 $required，请先构建 $Version。" } }

$expected = (Select-String -Path $sums -Pattern "^([0-9A-Fa-f]{64})\s+\*?$([regex]::Escape($exeName))$").Matches[0].Groups[1].Value
if (-not $expected) { throw "SHA256SUMS.txt 中没有 $exeName。" }
$actual = (Get-FileHash $exe -Algorithm SHA256).Hash
if ($actual -ne $expected.ToUpperInvariant()) { throw "$exeName 的 SHA-256 与 SHA256SUMS.txt 不一致。" }

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'ClipboardManager*' -and $_.ProcessName -notlike '*Maintenance*' })
foreach ($process in $running) {
    Write-Output "结束 $($process.ProcessName)（PID $($process.Id)）"
    $process.Kill()
    $null = $process.WaitForExit(10000)
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
$target = Join-Path $InstallDir "ClipboardManager.exe"
Copy-Item $exe $target -Force
Copy-Item $helper (Join-Path $InstallDir "ClipboardManager.Maintenance.exe") -Force
Get-ChildItem $InstallDir -Filter "*.old" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
if ((Get-FileHash $target -Algorithm SHA256).Hash -ne $actual) { throw "复制后的 EXE 校验失败。" }

$legacy = @(Get-ChildItem 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.DisplayName -eq '本地剪切板管理工具' -and $_.UninstallString -match 'MsiExec' })
foreach ($product in $legacy) {
    $code = $product.PSChildName
    if ($RemoveLegacyMsi) {
        $uninstall = Start-Process msiexec.exe -ArgumentList '/x', $code, '/qn', '/norestart' -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw "卸载旧版 $($product.DisplayVersion)（$code）失败，退出码 $($uninstall.ExitCode)。" }
        Write-Output "已卸载旧 MSI 版本 $($product.DisplayVersion)"
    } else {
        Write-Warning "仍装有旧 MSI 版本 $($product.DisplayVersion)，从它启动并保存设置会把开机启动项改回旧版。加 -RemoveLegacyMsi 重跑可卸载。"
    }
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "本地剪切板管理工具.lnk"
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $target
$link.WorkingDirectory = $InstallDir
$link.IconLocation = "$target,0"
$link.Save()
Write-Output "开始菜单快捷方式已指向 $target"

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ((Get-Item $runKey).GetValueNames() -contains "ClipboardManager") {
    Set-ItemProperty $runKey -Name "ClipboardManager" -Value "`"$target`""
    Write-Output "开机启动已改为 $target"
}

if (-not $NoStart) { Start-Process $target -WorkingDirectory $InstallDir }
Write-Output "已替换为 $Version：$target"
