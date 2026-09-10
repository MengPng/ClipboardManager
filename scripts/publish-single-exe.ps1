param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "artifacts/ClipboardManager.exe",
    [string]$Version = "0.6.6"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/ClipboardManager.Windows/ClipboardManager.Windows.csproj"
$maintenanceProj = Join-Path $root "tools/ClipboardManager.Maintenance/ClipboardManager.Maintenance.csproj"
$tempPublish = Join-Path $root "artifacts/publish/single-exe"
$maintenancePublish = Join-Path $root "artifacts/publish/maintenance-single-exe"

if (Test-Path $tempPublish) {
    Remove-Item $tempPublish -Recurse -Force
}
if (Test-Path $maintenancePublish) {
    Remove-Item $maintenancePublish -Recurse -Force
}

Write-Output "正在发布备份恢复助手..."
dotnet publish $maintenanceProj `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:NuGetAudit=false `
    -p:Version=$Version `
    -p:UseSharedCompilation=false `
    --output $maintenancePublish

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$maintenanceExe = Join-Path $maintenancePublish "ClipboardManager.Maintenance.exe"
if (-not (Test-Path $maintenanceExe)) {
    throw "单文件发布未生成 ClipboardManager.Maintenance.exe"
}

Write-Output "正在发布内置恢复能力的 Windows x64 单文件程序..."
dotnet publish $proj `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:NuGetAudit=false `
    -p:Version=$Version `
    -p:UseSharedCompilation=false `
    -p:MaintenanceHelperPath=$maintenanceExe `
    --output $tempPublish

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$sourceExe = Join-Path $tempPublish "ClipboardManager.exe"
if (-not (Test-Path $sourceExe)) {
    throw "单文件发布未生成 ClipboardManager.exe"
}

$destFile = Join-Path $root $OutputPath
$destDir = Split-Path -Parent $destFile
if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

Copy-Item $sourceExe $destFile -Force
$maintenanceDest = Join-Path $destDir "ClipboardManager.Maintenance.exe"
Copy-Item $maintenanceExe $maintenanceDest -Force
$hash = (Get-FileHash $destFile -Algorithm SHA256).Hash
"$hash  $(Split-Path -Leaf $destFile)" | Set-Content "$destFile.sha256" -Encoding ascii
$maintenanceHash = (Get-FileHash $maintenanceDest -Algorithm SHA256).Hash
"$maintenanceHash  $(Split-Path -Leaf $maintenanceDest)" | Set-Content "$maintenanceDest.sha256" -Encoding ascii

Write-Output "成功生成单文件 EXE: $destFile"
Write-Output "文件大小: $([math]::Round((Get-Item $destFile).Length / 1MB, 2)) MB"
Write-Output "SHA256: $hash"
Write-Output "恢复助手: $maintenanceDest"
Write-Output "恢复助手 SHA256: $maintenanceHash"
