param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "artifacts/ClipboardManager.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/ClipboardManager.Windows/ClipboardManager.Windows.csproj"
$tempPublish = Join-Path $root "artifacts/publish/single-exe"

if (Test-Path $tempPublish) {
    Remove-Item $tempPublish -Recurse -Force
}

Write-Output "正在发布 Windows x64 单文件自包含可执行程序..."
dotnet publish $proj `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:UseSharedCompilation=false `
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
$hash = (Get-FileHash $destFile -Algorithm SHA256).Hash
"$hash  $(Split-Path -Leaf $destFile)" | Set-Content "$destFile.sha256" -Encoding ascii

Write-Output "成功生成单文件 EXE: $destFile"
Write-Output "文件大小: $([math]::Round((Get-Item $destFile).Length / 1MB, 2)) MB"
Write-Output "SHA256: $hash"
