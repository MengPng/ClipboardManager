param(
    [string]$Output = "artifacts/publish/win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root $Output
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
dotnet publish (Join-Path $root "src/ClipboardManager.Windows/ClipboardManager.Windows.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:UseSharedCompilation=false -p:PublishSingleFile=false --output $target
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $target "ClipboardManager.exe"
if (-not (Test-Path $exe)) { throw "发布未生成 ClipboardManager.exe" }
$maintenance = Join-Path $target "maintenance"
dotnet publish (Join-Path $root "tools/ClipboardManager.Maintenance/ClipboardManager.Maintenance.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:UseSharedCompilation=false -p:PublishSingleFile=false --output $maintenance
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path (Join-Path $maintenance "ClipboardManager.Maintenance.exe"))) { throw "发布未生成离线恢复工具" }
$manifest = [ordered]@{
    product = "ClipboardManager"
    version = (Get-Item $exe).VersionInfo.ProductVersion
    runtime = "win-x64"
    selfContained = $true
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    fileCount = @(Get-ChildItem $target -File -Recurse).Count
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $target "release-manifest.json") -Encoding UTF8
Write-Output "Published $exe"
